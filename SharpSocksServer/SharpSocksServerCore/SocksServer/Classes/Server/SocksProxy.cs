﻿using System;
using SocksServer.Classes.Socks;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SharpSocksServer.ImplantCommsHTTPServer.Interfaces;
using Common.Server.Interfaces;
using System.Threading;
using SharpSocksServer.Source.UI.Classes;
using SharpSocksServer.ServerComms;
using System.Collections.Concurrent;

namespace SocksServer.Classes.Server
{
	public class SocksProxy
	{
		static readonly Int32 HEADERLIMIT = 65535;
		public uint TOTALSOCKETTIMEOUT { get; set; }
		static readonly int SOCKSCONNECTIONTOOPENTIMEOUT = 200000;
		public UInt64 Counter { get { return _instCounter; } }
		static UInt64 _nternalCounter = 0;
		UInt64 _instCounter = 0;
		static readonly object intlocker = new object();
		public static ILogOutput ServerComms { get; set; }
		public static ISocksImplantComms SocketComms { get; set; }
		AutoResetEvent TimeoutEvent = new AutoResetEvent(false);
		AutoResetEvent SocksTimeout = new AutoResetEvent(false);
		readonly object _shutdownLocker = new object();
		int CurrentlyReading = 0;
		String _targetHost;
		ushort _targetPort;
		String status = "closed";
		Int32 _dataSent = 0;
		Int32 _dataRecv = 0;
		DateTime? LastUpdateTime = null;
		String _targetId = null;
		bool ShutdownRecieved = false;
		bool _waitOnConnect = false;
		bool _open = false;

		TcpClient _tc;
		static ConcurrentDictionary<String, SocksProxy> mapTargetIdToSocksInstance = new ConcurrentDictionary<string, SocksProxy>();

		public SocksProxy()
		{
			lock (intlocker) { _instCounter  = ++_nternalCounter;  }
		}

		public static List<ConnectionDetails> ConnectionDetails
        {
            get
            {
                return mapTargetIdToSocksInstance.Keys.ToList().Select(x => 
                    new ConnectionDetails() {
                        HostPort = $"{mapTargetIdToSocksInstance[x]._targetPort}:{mapTargetIdToSocksInstance[x]._targetPort}",
                        DataRecv = mapTargetIdToSocksInstance[x]._dataRecv,
                        DataSent = mapTargetIdToSocksInstance[x]._dataSent,
                        TargetId  = mapTargetIdToSocksInstance[x]._targetId,
						Id = mapTargetIdToSocksInstance[x].Counter,
						Status = mapTargetIdToSocksInstance[x].status,
                        UpdateTime = (mapTargetIdToSocksInstance[x].LastUpdateTime.HasValue) ? mapTargetIdToSocksInstance[x].LastUpdateTime.Value.ToShortDateString() : "Never"
                    }
                ).ToList();
            }
        }

		public static ConnectionDetails GetDetailsForTargetId(string targetId)
		{
			if (mapTargetIdToSocksInstance.ContainsKey(targetId))
			{
				var dtls = mapTargetIdToSocksInstance[targetId];
				return new ConnectionDetails()
				{
					Id = dtls.Counter,
					HostPort = $"{dtls._targetHost}:{dtls._targetPort}",
					DataRecv = dtls._dataRecv,
					DataSent = dtls._dataSent,
					TargetId = dtls._targetId,
					Status = dtls.status,
					UpdateTime = (dtls.LastUpdateTime.HasValue) ? dtls.LastUpdateTime.Value.ToShortDateString() : "Never"
				};
			}
			return null;
		}

		public static bool ReturnDataCallback(String target, List<byte> payload)
        {
            if (!mapTargetIdToSocksInstance.ContainsKey(target))
            {
                ServerComms.LogError($"Target {target} not found in Socks instance");
                return false;
            }

            var socksInstance = mapTargetIdToSocksInstance[target];
            socksInstance.WriteResponseBackToClient(payload);
            return true;
        }

        public static void NotifyConnection(String target, String status)
        {
            if (ServerComms.IsVerboseOn())
                ServerComms.LogMessage($"Message has arrived back for {target}");

            if (!mapTargetIdToSocksInstance.ContainsKey(target))
            {
                ServerComms.LogError($"Target {target} not found in Socks instance");
                return;
            }
            var socksInstance = mapTargetIdToSocksInstance[target];

            if (status.ToLower() == "open")
                socksInstance._open = true;
            else
                socksInstance._open = false;

            socksInstance.status = status;
            socksInstance.LastUpdateTime = DateTime.Now;

            if (socksInstance._waitOnConnect)
                socksInstance.SocksTimeout.Set();
        }
        
        public static void ImplantCalledClose(String targetId)
        {
			if (mapTargetIdToSocksInstance.ContainsKey(targetId))
			{
				var socksInstance = mapTargetIdToSocksInstance[targetId];
				socksInstance.ShutdownClient(true);
			}
        }

		public static bool IsSessionOpen(String targetId)
		{
			if (mapTargetIdToSocksInstance.ContainsKey(targetId))
				return mapTargetIdToSocksInstance[targetId].status == "open";
			return false;
		}

        public static bool IsValidSession(String targetId)
        {
            return mapTargetIdToSocksInstance.ContainsKey(targetId);
        }

        void ShutdownClient(bool implantNotified = false)
        {
            status = (implantNotified) ? "closing (implant called close)" : "closing (SOCKS timeout)";
            LastUpdateTime = DateTime.Now;
            _open = false;
			if (!ShutdownRecieved)
			{
				lock (_shutdownLocker)
				{
					if (!ShutdownRecieved)
					{
						ShutdownRecieved = true;
						if (null != _tc && _tc.Connected)
							_tc.Close();

						if (!String.IsNullOrWhiteSpace(_targetId) && !implantNotified)
							SocketComms.CloseTargetConnection(_targetId);
					}
				}
			}
			if (mapTargetIdToSocksInstance.ContainsKey(_targetId))
			{
				if (!mapTargetIdToSocksInstance.TryRemove(_targetId, out SocksProxy scks))
					ServerComms.LogError($"Couldn't mark {_targetId} as an invalid session");
			}

			if (null == TimeoutEvent)
			{
				TimeoutEvent.Close();
				TimeoutEvent = null;
			}
		}

        public void ProcessRequest(TcpClient tc, bool waitOnConnect = false)
        {
            _tc = tc;
            var stream = _tc.GetStream();
            _waitOnConnect = waitOnConnect;

            if (!stream.CanRead)
            {
				if (tc.Client.RemoteEndPoint is IPEndPoint enp)
					ServerComms.LogError($"Failed reading SOCKS Connection from {IPAddress.Parse(enp.Address.ToString())}:{enp.Port.ToString()}");
				return;
            }

			try
			{
				var bytesRead = 0;
				var lstBuffer = new List<byte>();
				var arrayBuffer = new byte[512000];

				bytesRead = stream.Read(arrayBuffer, 0, 512000);
				lstBuffer.AddRange(arrayBuffer.Take(bytesRead));
				
				while (stream.CanRead && stream.DataAvailable)
				{
					bytesRead = stream.Read(arrayBuffer, 0, 512000);
					lstBuffer.AddRange(arrayBuffer.Take(bytesRead));
				}

				var procResult = ProcessSocksHeaders(lstBuffer.ToList());
				var responsePacket = BuildSocks4Response(procResult);

				stream.Write(responsePacket.ToArray(), 0, responsePacket.Count);
				stream.Flush();

				if (Socks4ClientHeader.Socks4ClientHeaderStatus.REQUEST_GRANTED == procResult)
					System.Threading.Tasks.Task.Factory.StartNew(() => StartCommsWithProxyAndImplant(stream));
				else
					_tc.Close();
				return;
			}
			catch
			{
				if (tc.Client.RemoteEndPoint is IPEndPoint enp)
					ServerComms.LogError($"Failed reading SOCKS Connection from {IPAddress.Parse(enp.Address.ToString())}:{enp.Port.ToString()}");
			}

        }
        public class AsyncBufferState
        {
            public byte[] Buffer { get; set; }
            public NetworkStream Stream;
			public AutoResetEvent RecvdData { get; set; }
        }

        void StartCommsWithProxyAndImplant(NetworkStream stream)
        {
            var timeoutd = false;
			IAsyncResult result = null;
			var buf = new byte[1];
			try 
			{
				var asyncBufferState = new AsyncBufferState() { Buffer = new Byte[HEADERLIMIT], Stream = stream, RecvdData = new AutoResetEvent(false) };
				var dataRecvd = asyncBufferState.RecvdData;
				var ctr = 0;
				while (!ShutdownRecieved)
				{
					try
					{
						if (ShutdownRecieved)
							return;

						//Use peek to try and force an exception if the connection has closed
						_tc.Client.Receive(buf, SocketFlags.Peek);
						if (stream.CanRead && stream.DataAvailable)
						{
							result = stream.BeginRead(asyncBufferState.Buffer, 0, HEADERLIMIT, ProxySocketReadCallback, asyncBufferState);
							timeoutd = !dataRecvd.WaitOne((int)TOTALSOCKETTIMEOUT);
							ctr = 0;
						}
						else
							TimeoutEvent.WaitOne(50);
						
					}
					catch (Exception ex)
					{
						ServerComms.LogError($"Connection to {_targetHost}:{_targetPort} has dropped: {ex.Message}");
						ShutdownClient();
						return;
					}
				
					if (ctr++ >= ((int)TOTALSOCKETTIMEOUT / 100))
					{
						stream.Close();
						//Time out trying to read may as well shutdown the socket
						ServerComms.LogError($"Connection closed to {_targetHost}:{_targetPort} after ({TOTALSOCKETTIMEOUT / 1000}s) idle. {_targetId}");
						ShutdownClient();
						return;
					}
				}
			}
            catch (Exception ex)
            {
                ServerComms.LogError($"Connection to {_targetHost}:{_targetPort} has dropped: {ex.Message}");
                ShutdownClient();
			}
		}

        void ProxySocketReadCallback(IAsyncResult iar)
        {
            var asyncState = (AsyncBufferState)iar.AsyncState;
			try
			{
				var stream = asyncState.Stream;
				int bytesRead = stream.EndRead(iar);
				if (ShutdownRecieved || null == _tc)
					return;

				if (!_tc.Connected)
				{
					ShutdownClient();
					try
					{
						if (_tc.Client != null)
							ServerComms.LogError($"Connection to {_tc.Client.RemoteEndPoint.ToString()} closed");
					}
					catch (ObjectDisposedException)
					{
						ServerComms.LogError($"Connection to {_targetHost}:{_targetPort} closed");
					}
					return;
				}   
                if (bytesRead > 0)
                { 
                    var payload = new List<byte>();
                    payload.AddRange(asyncState.Buffer.Take(bytesRead));
                    while (stream.CanRead && stream.DataAvailable )
						if ((bytesRead = stream.Read(asyncState.Buffer, 0, HEADERLIMIT)) > 0)
							payload.AddRange(asyncState.Buffer.Take(bytesRead));

                    SocketComms.SendDataToTarget(_targetId, payload);
					ServerComms.LogMessage($"Client sent data (size: {payload.Count}) {_targetId} writing to Implant");
					_dataSent += payload.Count;
				}
			}
            catch (Exception ex)
            {
				try
                {
                    if (_tc.Client != null)
						ServerComms.LogError($"Connection to {_tc.Client.RemoteEndPoint.ToString()} has dropped cause {ex.Message}");
                }
                catch (ObjectDisposedException)
                {
                    ServerComms.LogError($"Connection to {_targetHost}:{_targetPort} has dropped cause {ex.Message}");
                }
				ShutdownClient();
				
			}
			finally 
			{
				asyncState.RecvdData.Set();
			}
        }

        void WriteResponseBackToClient(List<byte> payload)
        {
            _dataRecv += payload.Count();
            
            if (_tc.Connected)
            {
				try
				{
					var stream = _tc.GetStream();
					stream.Write(payload.ToArray(), 0, payload.Count);
					stream.Flush();
					ServerComms.LogMessage($"Recieved payload back from Implant (size: {payload.Count} for {_targetId} writing to client");
					if (ServerComms.IsVerboseOn())
						ServerComms.LogMessage($"Wrote {payload.Count} ");
					if (!_tc.Connected)
						ShutdownClient();
				}
				catch (Exception ex)
				{
					ServerComms.LogMessage($"ERROR Writing data back to {ex.Message}");
					ShutdownClient();
				}
            }
            else
				ShutdownClient();
        }

        List<byte> BuildSocks4Response(byte responeCode)
        {
            var resp = new List<byte>() {0x0, responeCode };
            var ran = new Random((Int32)DateTime.Now.Ticks);
            resp.AddRange(BitConverter.GetBytes(ran.Next()).Take(2).ToArray());
            resp.AddRange(BitConverter.GetBytes(ran.Next()).ToArray());

            return resp;
        }

        byte ProcessSocksHeaders(List<byte> buffer)
        {
            if (9 > buffer.Count || 256 < buffer.Count)
            {
                ServerComms.LogError($"Socks server: buffer size {buffer.Count} is not valid, must be between 9 & 256");
                return Socks4ClientHeader.Socks4ClientHeaderStatus.REQUEST_REJECTED_OR_FAILED;
            }
            byte version = buffer[0];
            if (version == 0x4)
            {
                byte commandCode = buffer.Skip(1).Take(1).First();
                BitConverter.ToUInt16(buffer.Skip(2).Take(2).ToArray(), 0);
                BitConverter.ToUInt16(buffer.Skip(2).Take(2).Reverse().ToArray(), 0);
                _targetPort = BitConverter.ToUInt16(buffer.Skip(2).Take(2).Reverse().ToArray(), 0);
                var dstIp = buffer.Skip(4).Take(4).ToArray();

                var tailBuffer = buffer.Skip(8);
                var endUserIdx = tailBuffer.ToList().IndexOf(0x0) + 1;
                if (-1 == endUserIdx)
                {
                    ServerComms.LogError($"User id is invalid rejecting connection request");
                    return Socks4ClientHeader.Socks4ClientHeaderStatus.REQUEST_REJECTED_OR_FAILED;
                }
                var userId = UTF8Encoding.UTF8.GetString(tailBuffer.Take(endUserIdx).ToArray());

                //Check if SOCKS 4a and domain name specified 
                //If the domain name is to follow the IP will be in the format 0.0.0.x
                if (0 == dstIp[0] && 0 == dstIp[1] && 0 == dstIp[2] && 0 != dstIp[3])
                {

                    var endHostIdx = tailBuffer.Skip(endUserIdx).ToList().IndexOf(0x0);
                    var arrayHost = tailBuffer.Skip(endUserIdx).Take(endHostIdx).ToArray();
                    if (arrayHost.Length == 0)
                    {
                        ServerComms.LogError($"Host name is empty rejecting connection request");
                        return Socks4ClientHeader.Socks4ClientHeaderStatus.REQUEST_REJECTED_OR_FAILED;
                    }
                    var dnsHost = UTF8Encoding.UTF8.GetString(arrayHost);
                    if(UriHostNameType.Unknown == Uri.CheckHostName(dnsHost))
                    {
                        ServerComms.LogError($"Host name {dnsHost} is invalid rejecting connection request");
                        return Socks4ClientHeader.Socks4ClientHeaderStatus.REQUEST_REJECTED_OR_FAILED;
                    }
                    _targetHost = dnsHost;
                }
                else
                    _targetHost = new IPAddress(BitConverter.ToUInt32(dstIp, 0)).ToString();

               ServerComms.LogMessage($"SOCKS Request to open {_targetHost}:{_targetPort}");
                status = "opening";
                LastUpdateTime = DateTime.Now;

                _targetId = SocketComms.CreateNewConnectionTarget(_targetHost, _targetPort);
                var thisptr = this;
                if (null == thisptr)
                    ServerComms.LogError("This pointer is NULL something wrong here");

				mapTargetIdToSocksInstance.TryAdd(_targetId, thisptr);
                
                if(_waitOnConnect)
                {
                    SocksTimeout.WaitOne(SOCKSCONNECTIONTOOPENTIMEOUT);
                    if (!_open)
                        return Socks4ClientHeader.Socks4ClientHeaderStatus.REQUEST_REJECTED_OR_FAILED;
                }
            }
            else
                return Socks4ClientHeader.Socks4ClientHeaderStatus.REQUEST_REJECTED_OR_FAILED;

            ServerComms.LogMessage($"Opened SOCKS port {_targetHost}:{_targetPort} targetid {_targetId}");
            return Socks4ClientHeader.Socks4ClientHeaderStatus.REQUEST_GRANTED;
        }
    }
}