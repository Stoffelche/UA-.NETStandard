/* Copyright (c) 1996-2022 The OPC Foundation. All rights reserved.
   The source code in this file is covered under a dual-license scenario:
     - RCL: for OPC Foundation Corporate Members in good-standing
     - GPL V2: everybody else
   RCL license terms accompanied with this source code. See http://opcfoundation.org/License/RCL/1.00/
   GNU General Public License as published by the Free Software Foundation;
   version 2 of the License are accompanied with this source code. See http://opcfoundation.org/License/GPLv2
   This source code is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
*/

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Opc.Ua.Bindings
{
    /// <summary>
    /// Manages the listening side of a UA TCP channel.
    /// </summary>
    public class TcpListenerChannel : UaSCUaBinaryChannel
    {
        #region Constructors
        /// <summary>
        /// Attaches the object to an existing socket.
        /// </summary>
        public TcpListenerChannel(
            string contextId,
            ITcpChannelListener listener,
            BufferManager bufferManager,
            ChannelQuotas quotas,
            X509Certificate2 serverCertificate,
            EndpointDescriptionCollection endpoints)
        :
            this(contextId, listener, bufferManager, quotas, serverCertificate, null, endpoints)
        {
        }

        /// <summary>
        /// Attaches the object to an existing socket.
        /// </summary>
        public TcpListenerChannel(
            string contextId,
            ITcpChannelListener listener,
            BufferManager bufferManager,
            ChannelQuotas quotas,
            X509Certificate2 serverCertificate,
            X509Certificate2Collection serverCertificateChain,
            EndpointDescriptionCollection endpoints)
        :
            base(contextId, bufferManager, quotas, serverCertificate, serverCertificateChain, endpoints, MessageSecurityMode.None, SecurityPolicies.None)
        {
            m_listener = listener;
        }
        #endregion

        #region IDisposable Members
        /// <summary>
        /// An overrideable version of the Dispose.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "m_cleanupTimer")]
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Utils.SilentDispose(m_cleanupTimer);
                m_cleanupTimer = null;
            }

            base.Dispose(disposing);
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// The channel name used in trace output.
        /// </summary>
        public virtual string ChannelName => "TCPLISTENERCHANNEL";

        /// <summary>
        /// The TCP channel listener.
        /// </summary>
        protected ITcpChannelListener Listener => m_listener;

        /// <summary>
        /// Sets the callback used to receive notifications of new events.
        /// </summary>
        public void SetRequestReceivedCallback(TcpChannelRequestEventHandler callback)
        {
            lock (DataLock)
            {
                m_requestReceived = callback;
            }
        }

        /// <summary>
        /// Sets the callback used to raise channel audit events.
        /// </summary>
        public void SetReportOpenSecureChannellAuditCalback(ReportAuditOpenSecureChannelEventHandler callback)
        {
            lock (DataLock)
            {
                m_reportAuditOpenSecureChannelEvent = callback;
            }
        }

        /// <summary>
        /// Sets the callback used to raise channel audit events.
        /// </summary>
        public void SetReportCloseSecureChannellAuditCalback(ReportAuditCloseSecureChannelEventHandler callback)
        {
            lock (DataLock)
            {
                m_reportAuditCloseSecureChannelEvent = callback;
            }
        }

        /// <summary>
        /// Sets the callback used to raise channel audit events.
        /// </summary>
        public void SetReportCertificateAuditCalback(ReportAuditCertificateEventHandler callback)
        {
            lock (DataLock)
            {
                m_reportAuditCertificateEvent = callback;
            }
        }

        /// <summary>
        /// Attaches the channel to an existing socket.
        /// </summary>
        public void Attach(uint channelId, Socket socket)
        {
            if (socket == null) throw new ArgumentNullException(nameof(socket));

            lock (DataLock)
            {
                // check for existing socket.
                if (Socket != null)
                {
                    throw new InvalidOperationException("Channel is already attached to a socket.");
                }

                ChannelId = channelId;
                State = TcpChannelState.Connecting;

                Socket = new TcpMessageSocket(this, socket, BufferManager, Quotas.MaxBufferSize);

                Utils.LogInfo("{0} SOCKET ATTACHED: {1:X8}, ChannelId={2}", ChannelName, Socket.Handle, ChannelId);

                Socket.ReadNextMessage();

                // automatically clean up the channel if no hello received.
                StartCleanupTimer(StatusCodes.BadTimeout);
            }
        }
        #endregion

        #region Socket Event Handlers
        #endregion

        #region Error Handling Functions
        /// <summary>
        /// Handles a socket error.
        /// </summary>
        protected override void HandleSocketError(ServiceResult result)
        {
            lock (DataLock)
            {
                // channel fault.
                if (ServiceResult.IsBad(result))
                {
                    ForceChannelFault(result);
                    return;
                }

                // gracefully shutdown the channel.
                ChannelClosed();
            }
        }

        /// <summary>
        /// Forces the channel into a faulted state as a result of a fatal error.
        /// </summary>
        protected void ForceChannelFault(uint statusCode, string format, params object[] args)
        {
            ForceChannelFault(ServiceResult.Create(statusCode, format, args));
        }

        /// <summary>
        /// Forces the channel into a faulted state as a result of a fatal error.
        /// </summary>
        protected void ForceChannelFault(Exception exception, uint defaultCode, string format, params object[] args)
        {
            ForceChannelFault(ServiceResult.Create(exception, defaultCode, format, args));
        }

        /// <summary>
        /// Forces the channel into a faulted state as a result of a fatal error.
        /// </summary>
        protected void ForceChannelFault(ServiceResult reason)
        {
            lock (DataLock)
            {
                Utils.LogError(
                    "{0} ForceChannelFault Socket={1:X8}, ChannelId={2}, TokenId={3}, Reason={4}",
                    ChannelName,
                    (Socket != null) ? Socket.Handle : 0,
                    (CurrentToken != null) ? CurrentToken.ChannelId : 0,
                    (CurrentToken != null) ? CurrentToken.TokenId : 0,
                    reason);

                CompleteReverseHello(new ServiceResultException(reason));

                // nothing to do if channel already in a faulted state.
                if (State == TcpChannelState.Faulted)
                {
                    return;
                }

                // send error and close response.
                if (Socket != null)
                {
                    if (m_responseRequired)
                    {
                        SendErrorMessage(reason);
                    }
                }

                State = TcpChannelState.Faulted;
                m_responseRequired = false;

                // notify any monitors.
                NotifyMonitors(reason, false);

                // ensure the channel will be cleaned up if the client does not reconnect.
                StartCleanupTimer(reason);
            }
        }

        /// <summary>
        /// Starts a timer that will clean up the channel if it is not opened/re-opened.
        /// </summary>
        protected void StartCleanupTimer(ServiceResult reason)
        {
            CleanupTimer();
            m_cleanupTimer = new Timer(OnCleanup, reason, Quotas.ChannelLifetime, Timeout.Infinite);
        }

        /// <summary>
        /// Cleans up a timer that will clean up the channel if it is not opened/re-opened.
        /// </summary>
        protected void CleanupTimer()
        {
            if (m_cleanupTimer != null)
            {
                m_cleanupTimer.Dispose();
                m_cleanupTimer = null;
            }
        }

        /// <summary>
        /// Called when the channel needs to be cleaned up.
        /// </summary>
        private void OnCleanup(object state)
        {
            lock (DataLock)
            {
                CleanupTimer();
                // nothing to do if the channel is now open or closed.
                if (State == TcpChannelState.Closed || State == TcpChannelState.Open)
                {
                    return;
                }

                // get reason for cleanup.
                ServiceResult reason = state as ServiceResult;

                if (reason == null)
                {
                    reason = new ServiceResult(StatusCodes.BadTimeout);
                }

                Utils.LogInfo(
                    "{0} Cleanup Socket={1:X8}, ChannelId={2}, TokenId={3}, Reason={4}",
                    ChannelName,
                    (Socket != null) ? Socket.Handle : 0,
                    (CurrentToken != null) ? CurrentToken.ChannelId : 0,
                    (CurrentToken != null) ? CurrentToken.TokenId : 0,
                    reason.ToString());

                // close channel.
                ChannelClosed();
            }
        }

        /// <summary>
        /// Closes the channel and releases resources.
        /// </summary>
        protected void ChannelClosed()
        {
            try
            {
                if (Socket != null)
                {
                    Socket.Close();
                }
            }
            finally
            {
                State = TcpChannelState.Closed;
                m_listener.ChannelClosed(ChannelId);

                // notify any monitors.
                NotifyMonitors(new ServiceResult(StatusCodes.BadConnectionClosed), true);

                CleanupTimer();
            }
        }

        /// <summary>
        /// Sends an error message over the socket.
        /// </summary>
        protected void SendErrorMessage(ServiceResult error)
        {
            Utils.LogTrace("ChannelId {0}: SendErrorMessage={1}", ChannelId, error.StatusCode);

            byte[] buffer = BufferManager.TakeBuffer(SendBufferSize, "SendErrorMessage");

            try
            {
                using (BinaryEncoder encoder = new BinaryEncoder(buffer, 0, SendBufferSize, Quotas.MessageContext))
                {
                    encoder.WriteUInt32(null, TcpMessageType.Error);
                    encoder.WriteUInt32(null, 0);

                    WriteErrorMessageBody(encoder, error);

                    int size = encoder.Close();
                    UpdateMessageSize(buffer, 0, size);

                    BeginWriteMessage(new ArraySegment<byte>(buffer, 0, size), null);
                    buffer = null;
                }
            }
            finally
            {
                if (buffer != null)
                {
                    BufferManager.ReturnBuffer(buffer, "SendErrorMessage");
                }
            }
        }

        /// <summary>
        /// Sends a fault response secured with the symmetric keys.
        /// </summary>
        protected void SendServiceFault(ChannelToken token, uint requestId, ServiceResult fault)
        {
            Utils.LogTrace("ChannelId {0}: Request {1}: SendServiceFault={2}", ChannelId, requestId, fault.StatusCode);

            BufferCollection buffers = null;

            try
            {
                // construct fault.
                ServiceFault response = new ServiceFault();

                response.ResponseHeader.ServiceResult = fault.Code;

                StringTable stringTable = new StringTable();

                response.ResponseHeader.ServiceDiagnostics = new DiagnosticInfo(
                    fault,
                    DiagnosticsMasks.NoInnerStatus,
                    true,
                    stringTable);

                response.ResponseHeader.StringTable = stringTable.ToArray();

                // the limits should never be exceeded when sending a fault.
                bool limitsExceeded = false;

                // secure message.
                buffers = WriteSymmetricMessage(
                    TcpMessageType.Message,
                    requestId,
                    token,
                    response,
                    false,
                    out limitsExceeded);

                // send message.
                BeginWriteMessage(buffers, null);
                buffers = null;
            }
            catch (Exception e)
            {
                if (buffers != null)
                {
                    buffers.Release(BufferManager, "SendServiceFault");
                }

                ForceChannelFault(ServiceResult.Create(e, StatusCodes.BadTcpInternalError, "Unexpected error sending a service fault."));
            }
        }

        /// <summary>
        /// Notify if the channel status changed.
        /// </summary>
        protected virtual void NotifyMonitors(ServiceResult status, bool closed)
        {
            // intentionally left empty
        }

        /// <summary>
        /// Called to indicate an error or success if the listener 
        /// channel initiated a reverse hello connection.
        /// </summary>
        /// <remarks>
        /// The callback is only used by the server channel.
        /// The listener channel uses the callback to indicate
        /// an error condition to the server channel.
        /// </remarks>
        protected virtual void CompleteReverseHello(Exception e)
        {
            // intentionally left empty
        }

        /// <summary>
        /// Sends a fault response secured with the asymmetric keys.
        /// </summary>
        protected void SendServiceFault(uint requestId, ServiceResult fault)
        {
            Utils.LogTrace("ChannelId {0}: Request {1}: SendServiceFault={2}", ChannelId, requestId, fault.StatusCode);

            BufferCollection chunksToSend = null;

            try
            {
                // construct fault.
                ServiceFault response = new ServiceFault();

                response.ResponseHeader.ServiceResult = fault.Code;

                StringTable stringTable = new StringTable();

                response.ResponseHeader.ServiceDiagnostics = new DiagnosticInfo(
                    fault,
                    DiagnosticsMasks.NoInnerStatus,
                    true,
                    stringTable);

                response.ResponseHeader.StringTable = stringTable.ToArray();

                // serialize fault.
                byte[] buffer = BinaryEncoder.EncodeMessage(response, Quotas.MessageContext);

                // secure message.
                chunksToSend = WriteAsymmetricMessage(
                    TcpMessageType.Open,
                    requestId,
                    ServerCertificate,
                    ClientCertificate,
                    new ArraySegment<byte>(buffer, 0, buffer.Length));

                // write the message to the server.
                BeginWriteMessage(chunksToSend, null);
                chunksToSend = null;
            }
            catch (Exception e)
            {
                if (chunksToSend != null)
                {
                    chunksToSend.Release(BufferManager, "SendServiceFault");
                }

                ForceChannelFault(ServiceResult.Create(e, StatusCodes.BadTcpInternalError, "Unexpected error sending a service fault."));
            }
        }

        /// <summary>
        /// Handles a reconnect request.
        /// </summary>
        public virtual void Reconnect(IMessageSocket socket, uint requestId, uint sequenceNumber, X509Certificate2 clientCertificate, ChannelToken token, OpenSecureChannelRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Set the flag if a response is required for the use case of reverse connect.
        /// </summary>
        protected void SetResponseRequired(bool responseRequired)
        {
            m_responseRequired = responseRequired;
        }
        #endregion

        #region Connect/Reconnect Sequence
        /// <summary>
        /// Returns a new token id.
        /// </summary>
        protected uint GetNewTokenId()
        {
            return Utils.IncrementIdentifier(ref m_lastTokenId);
        }
        #endregion

        #region Protected Functions
        /// <summary>
        /// The channel request event handler.
        /// </summary>
        protected TcpChannelRequestEventHandler RequestReceived => m_requestReceived;

        /// <summary>
        /// The report open secure channel audit event handler.
        /// </summary>
        protected ReportAuditOpenSecureChannelEventHandler ReportAuditOpenSecureChannelEvent => m_reportAuditOpenSecureChannelEvent;

        /// <summary>
        /// The report close secure channel audit event handler.
        /// </summary>
        protected ReportAuditCloseSecureChannelEventHandler ReportAuditCloseSecureChannelEvent => m_reportAuditCloseSecureChannelEvent;

        /// <summary>
        /// The report certificate audit event handler.
        /// </summary>
        protected ReportAuditCertificateEventHandler ReportAuditCertificateEvent => m_reportAuditCertificateEvent;
        #endregion

        #region Private Fields
        private ITcpChannelListener m_listener;
        private bool m_responseRequired;
        private TcpChannelRequestEventHandler m_requestReceived;
        private ReportAuditOpenSecureChannelEventHandler m_reportAuditOpenSecureChannelEvent;
        private ReportAuditCloseSecureChannelEventHandler m_reportAuditCloseSecureChannelEvent;
        private ReportAuditCertificateEventHandler m_reportAuditCertificateEvent;
        private long m_lastTokenId;
        private Timer m_cleanupTimer;
        #endregion
    }

    /// <summary>
    /// Used to report an incoming request.
    /// </summary>
    public delegate void TcpChannelRequestEventHandler(TcpListenerChannel channel, uint requestId, IServiceRequest request);

    /// <summary>
    /// Used to report the status of the channel.
    /// </summary>
    public delegate void TcpChannelStatusEventHandler(TcpServerChannel channel, ServiceResult status, bool closed);

    /// <summary>
    /// Used to report an open secure channel audit event.
    /// </summary>
    public delegate void ReportAuditOpenSecureChannelEventHandler(TcpServerChannel channel, OpenSecureChannelRequest request, X509Certificate2 clientCertificate, Exception exception);

    /// <summary>
    /// Used to report a close secure channel audit event
    /// </summary>
    public delegate void ReportAuditCloseSecureChannelEventHandler(TcpServerChannel channel, Exception exception);

    /// <summary>
    /// Used to report an open secure channel audit event.
    /// </summary>
    public delegate void ReportAuditCertificateEventHandler(X509Certificate2 clientCertificate, Exception exception);

}
