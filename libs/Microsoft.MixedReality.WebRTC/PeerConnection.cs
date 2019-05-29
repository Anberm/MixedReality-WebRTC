using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

[assembly:InternalsVisibleTo("Microsoft.MixedReality.WebRTC.Tests")]

namespace Microsoft.MixedReality.WebRTC
{
    /// <summary>
    /// Attribute to decorate managed delegates used as native callbacks (reverse P/Invoke).
    /// Required by Mono in Ahead-Of-Time (AOT) compiling, and Unity with the IL2CPP backend.
    /// </summary>
    /// 
    /// This attribute is required by Mono AOT and Unity IL2CPP, but not by .NET Core or Framework.
    /// The implementation was copied from the Mono source code (https://github.com/mono/mono).
    /// The type argument does not seem to be used anywhere in the code, and a stub implementation
    /// like this seems to be enough for IL2CPP to be able to marshal the delegate (untested on Mono).
    [AttributeUsage(AttributeTargets.Method)]
    sealed class MonoPInvokeCallbackAttribute : Attribute
    {
        public MonoPInvokeCallbackAttribute(Type t) { }
    }

    /// <summary>
    /// The WebRTC peer connection object is the entry point to using WebRTC.
    /// </summary>
    public class PeerConnection : IDisposable
    {
        //public delegate void DataChannelMessageDelegate(byte[] data);
        //public delegate void DataChannelBufferingDelegate(ulong previous, ulong current, ulong limit);
        //public delegate void DataChannelStateDelegate(WebRTCDataChannel.State state);

        public delegate void LocalSdpReadyToSendDelegate(string type, string sdp);
        public delegate void IceCandidateReadytoSendDelegate(string candidate, int sdpMlineindex, string sdpMid);
        public delegate void VideoCaptureDeviceEnumCallback(string id, string name);

        /// <summary>
        /// Boolean property indicating whether the peer connection has been initialized.
        /// </summary>
        public bool Initialized
        {
            get
            {
                lock (_openCloseLock)
                {
                    return (_nativePeerhandle != IntPtr.Zero);
                }
            }
        }

        /// <summary>
        /// Event fired when a connection is established.
        /// </summary>
        public event Action Connected;

        /// <summary>
        /// Event that occurs when a local SDP message is ready to be transmitted.
        /// </summary>
        public event LocalSdpReadyToSendDelegate LocalSdpReadytoSend;

        /// <summary>
        /// Event that occurs when a local ICE candidate is ready to be transmitted.
        /// </summary>
        public event IceCandidateReadytoSendDelegate IceCandidateReadytoSend;

        /// <summary>
        /// Event that occurs when a renegotiation of the session is needed.
        /// This generally occurs as a result of adding or removing tracks,
        /// and the user should call <see cref="CreateOffer"/> to actually
        /// start a renegotiation.
        /// </summary>
        public event Action RenegotiationNeeded;

        /// <summary>
        /// Event that occurs when a video frame from a local track has been
        /// produced locally and is available for render.
        /// </summary>
        public event I420VideoFrameDelegate I420LocalVideoFrameReady;

        /// <summary>
        /// Event that occurs when a video frame from a remote peer has been
        /// received and is available for render.
        /// </summary>
        public event I420VideoFrameDelegate I420RemoteVideoFrameReady;

        /// <summary>
        /// Event that occurs when a video frame from a local track has been
        /// produced locally and is available for render.
        /// </summary>
        public event ARGBVideoFrameDelegate ARGBLocalVideoFrameReady;

        /// <summary>
        /// Event that occurs when a video frame from a remote peer has been
        /// received and is available for render.
        /// </summary>
        public event ARGBVideoFrameDelegate ARGBRemoteVideoFrameReady;

        /// <summary>
        /// Static wrappers for native callbacks to invoke instance methods on objects.
        /// </summary>
        private static class CallbacksWrappers
        {
            private delegate void VideoCaptureDeviceEnumDelegate(string id, string name, IntPtr handle);
            private delegate void VideoCaptureDeviceEnumCompletedDelegate(IntPtr handle);
            private delegate void ConnectedDelegate(IntPtr peer);
            private delegate void LocalSdpReadytoSendDelegate(IntPtr peer, string type, string sdp);
            private delegate void IceCandidateReadytoSendDelegate(IntPtr peer, string candidate, int sdpMlineindex, string sdpMid);
            private delegate void RenegotiationNeededDelegate(IntPtr peer);
            private delegate void DataChannelMessageDelegate(IntPtr peer, IntPtr data, ulong size);
            private delegate void DataChannelBufferingDelegate(IntPtr peer, ulong previous, ulong current, ulong limit);
            private delegate void DataChannelStateDelegate(IntPtr peer, int state, int id);

            public class EnumVideoCaptureWrapper
            {
                public VideoCaptureDeviceEnumCallback callback;
                public Action completedCallback;
            }

            [MonoPInvokeCallback(typeof(VideoCaptureDeviceEnumDelegate))]
            public static void VideoCaptureDeviceEnumCallback(string id, string name, IntPtr userData)
            {
                var handle = GCHandle.FromIntPtr(userData);
                var wrapper = (handle.Target as EnumVideoCaptureWrapper);
                wrapper.callback(id, name); // this is mandatory, never null
            }

            [MonoPInvokeCallback(typeof(VideoCaptureDeviceEnumCompletedDelegate))]
            public static void VideoCaptureDeviceEnumCompletedCallback(IntPtr userData)
            {
                var handle = GCHandle.FromIntPtr(userData);
                var wrapper = (handle.Target as EnumVideoCaptureWrapper);
                wrapper.completedCallback?.Invoke(); // this is optional, allows to be null
                handle.Free();
            }

            /// <summary>
            /// Utility to lock all delegates registered with the native plugin and prevent their garbage collection.
            /// </summary>
            /// <remarks>
            /// The delegate don't need to be pinned, just referenced to prevent garbage collection.
            /// So referencing them from this class is enough to keep them alive and usable.
            /// </remarks>
            public class PeerCallbackArgs
            {
                public PeerConnection Peer;
                public NativeMethods.PeerConnectionConnectedCallback ConnectedCallback;
                public NativeMethods.PeerConnectionLocalSdpReadytoSendCallback LocalSdpReadytoSendCallback;
                public NativeMethods.PeerConnectionIceCandidateReadytoSendCallback IceCandidateReadytoSendCallback;
                public NativeMethods.PeerConnectionRenegotiationNeededCallback RenegotiationNeededCallback;
                public NativeMethods.PeerConnectionI420VideoFrameCallback I420LocalVideoFrameCallback;
                public NativeMethods.PeerConnectionI420VideoFrameCallback I420RemoteVideoFrameCallback;
                public NativeMethods.PeerConnectionARGBVideoFrameCallback ARGBLocalVideoFrameCallback;
                public NativeMethods.PeerConnectionARGBVideoFrameCallback ARGBRemoteVideoFrameCallback;
            }

            public class DataChannelCallbackArgs
            {
                public PeerConnection Peer;
                //public WebRTCDataChannel DataChannel;
                public NativeMethods.PeerConnectionDataChannelMessageCallback MessageCallback;
                public NativeMethods.PeerConnectionDataChannelBufferingCallback BufferingCallback;
                public NativeMethods.PeerConnectionDataChannelStateCallback StateCallback;

                public static DataChannelCallbackArgs FromIntPtr(IntPtr userData)
                {
                    var handle = GCHandle.FromIntPtr(userData);
                    return (handle.Target as DataChannelCallbackArgs);
                }
            }

            [MonoPInvokeCallback(typeof(ConnectedDelegate))]
            public static void ConnectedCallback(IntPtr userData)
            {
                var peer = FromIntPtr(userData);
                peer.Connected?.Invoke();
            }

            [MonoPInvokeCallback(typeof(LocalSdpReadytoSendDelegate))]
            public static void LocalSdpReadytoSendCallback(IntPtr userData, string type, string sdp)
            {
                var peer = FromIntPtr(userData);
                peer.LocalSdpReadytoSend?.Invoke(type, sdp);
            }

            [MonoPInvokeCallback(typeof(IceCandidateReadytoSendDelegate))]
            public static void IceCandidateReadytoSendCallback(IntPtr userData, string candidate, int sdpMlineindex, string sdpMid)
            {
                var peer = FromIntPtr(userData);
                peer.IceCandidateReadytoSend?.Invoke(candidate, sdpMlineindex, sdpMid);
            }

            [MonoPInvokeCallback(typeof(RenegotiationNeededDelegate))]
            public static void RenegotiationNeededCallback(IntPtr userData)
            {
                var peer = FromIntPtr(userData);
                peer.RenegotiationNeeded?.Invoke();
            }

            [MonoPInvokeCallback(typeof(I420VideoFrameDelegate))]
            public static void I420LocalVideoFrameCallback(IntPtr userData,
                IntPtr dataY, IntPtr dataU, IntPtr dataV, IntPtr dataA,
                int strideY, int strideU, int strideV, int strideA,
                int width, int height)
            {
                var peer = FromIntPtr(userData);
                var frame = new I420AVideoFrame()
                {
                    width = (uint)width,
                    height = (uint)height,
                    dataY = dataY,
                    dataU = dataU,
                    dataV = dataV,
                    dataA = dataA,
                    strideY = strideY,
                    strideU = strideU,
                    strideV = strideV,
                    strideA = strideA
                };
                peer.I420LocalVideoFrameReady?.Invoke(frame);
            }

            [MonoPInvokeCallback(typeof(I420VideoFrameDelegate))]
            public static void I420RemoteVideoFrameCallback(IntPtr userData,
                IntPtr dataY, IntPtr dataU, IntPtr dataV, IntPtr dataA,
                int strideY, int strideU, int strideV, int strideA,
                int width, int height)
            {
                var peer = FromIntPtr(userData);
                var frame = new I420AVideoFrame()
                {
                    width = (uint)width,
                    height = (uint)height,
                    dataY = dataY,
                    dataU = dataU,
                    dataV = dataV,
                    dataA = dataA,
                    strideY = strideY,
                    strideU = strideU,
                    strideV = strideV,
                    strideA = strideA
                };
                peer.I420RemoteVideoFrameReady?.Invoke(frame);
            }

            [MonoPInvokeCallback(typeof(ARGBVideoFrameDelegate))]
            public static void ARGBLocalVideoFrameCallback(IntPtr userData,
                IntPtr data, int stride, int width, int height)
            {
                var peer = FromIntPtr(userData);
                var frame = new ARGBVideoFrame()
                {
                    width = (uint)width,
                    height = (uint)height,
                    data = data,
                    stride = stride
                };
                peer.ARGBLocalVideoFrameReady?.Invoke(frame);
            }

            [MonoPInvokeCallback(typeof(ARGBVideoFrameDelegate))]
            public static void ARGBRemoteVideoFrameCallback(IntPtr userData,
                IntPtr data, int stride, int width, int height)
            {
                var peer = FromIntPtr(userData);
                var frame = new ARGBVideoFrame()
                {
                    width = (uint)width,
                    height = (uint)height,
                    data = data,
                    stride = stride
                };
                peer.ARGBRemoteVideoFrameReady?.Invoke(frame);
            }

            //[MonoPInvokeCallback(typeof(DataChannelMessageDelegate))]
            //public static void DataChannelMessageCallback(IntPtr userData, IntPtr data, ulong size)
            //{
            //    var args = DataChannelCallbackArgs.FromIntPtr(userData);
            //    args.DataChannel.OnMessageReceived(data, size);
            //}

            //[MonoPInvokeCallback(typeof(DataChannelBufferingDelegate))]
            //public static void DataChannelBufferingCallback(IntPtr userData, ulong previous, ulong current, ulong limit)
            //{
            //    var args = DataChannelCallbackArgs.FromIntPtr(userData);
            //    args.DataChannel.OnBufferingChanged(previous, current, limit);
            //}

            //[MonoPInvokeCallback(typeof(DataChannelStateDelegate))]
            //public static void DataChannelStateCallback(IntPtr userData, int state, int id)
            //{
            //    var args = DataChannelCallbackArgs.FromIntPtr(userData);
            //    args.DataChannel.OnStateChanged((WebRTCDataChannel.State)state, id);
            //}
        }

        /// <summary>
        /// GCHandle to self for the various native callbacks.
        /// This also acts as a marker of a connection created or in the process of being created.
        /// </summary>
        private GCHandle _selfHandle;

        /// <summary>
        /// Handle to the native PeerConnection object.
        /// </summary>
        /// <remarks>
        /// In native land this is a <code>mrtk::net::webrtc_impl::PeerConnectionHandle</code>.
        /// </remarks>
        private IntPtr _nativePeerhandle = IntPtr.Zero;

        /// <summary>
        /// Initialization task returned by <see cref="InitializeAsync"/>.
        /// </summary>
        private Task _initTask = null;

        private CancellationTokenSource _initCTS = new CancellationTokenSource();

        /// <summary>
        /// Boolean to indicate if <see cref="Close"/> has been called and is waiting for a pending
        /// initializing task <see cref="_initTask"/> to complete or cancel.
        /// </summary>
        private bool _isClosing = false;

        /// <summary>
        /// Lock for asynchronous opening and closing of the connection, protecting
        /// changes to <see cref="_nativePeerhandle"/>, <see cref="_selfHandle"/>,
        /// <see cref="_initTask"/>, and <see cref="_isClosing"/>.
        /// </summary>
        private object _openCloseLock = new object();

        private CallbacksWrappers.PeerCallbackArgs _peerCallbackArgs;


        #region Initializing and shutdown

        /// <summary>
        /// Initialize the current peer connection object asynchronously.
        /// </summary>
        /// <param name="servers">List of STUN and TURN servers</param>
        /// <param name="username">Optional user name for server connection</param>
        /// <param name="credentials">Optional credentials for server connection</param>
        /// <returns>The singleton task used to initialize the underlying native peer connection.</returns>
        /// <remarks>This method is multi-thread safe, and will always return the same task object
        /// from the first call to it until the peer connection object is deinitialized. This allows
        /// multiple callers to all execute some action following the initialization, without the need
        /// to force a single caller and to synchronize with it.</remarks>
        public Task InitializeAsync(List<string> servers, string username,
            string credentials, CancellationToken token = default)
        {
            lock (_openCloseLock)
            {
                // If Close() is waiting for _initTask to finish, do not return it.
                if (_isClosing)
                {
                    throw new OperationCanceledException("A closing operation is pending.");
                }

                // If _initTask has already been created, return it.
                if (_initTask != null)
                {
                    return _initTask;
                }

                // Allocate a GC handle to self for static P/Invoke callbacks to be able to call
                // back into methods of this peer connection object. The handle is released when
                // the peer connection is closed, to allow this object to be destroyed.
                Debug.Assert(!_selfHandle.IsAllocated);
                _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);

                // Create and lock in memory delegates for all the static callback wrappers (see below).
                // This avoids delegates being garbage-collected, since the P/Invoke mechanism by itself
                // does not guarantee their lifetime.
                _peerCallbackArgs = new CallbacksWrappers.PeerCallbackArgs()
                {
                    Peer = this,
                    ConnectedCallback = CallbacksWrappers.ConnectedCallback,
                    LocalSdpReadytoSendCallback = CallbacksWrappers.LocalSdpReadytoSendCallback,
                    IceCandidateReadytoSendCallback = CallbacksWrappers.IceCandidateReadytoSendCallback,
                    RenegotiationNeededCallback = CallbacksWrappers.RenegotiationNeededCallback,
                    I420LocalVideoFrameCallback = CallbacksWrappers.I420LocalVideoFrameCallback,
                    I420RemoteVideoFrameCallback = CallbacksWrappers.I420RemoteVideoFrameCallback,
                    ARGBLocalVideoFrameCallback = CallbacksWrappers.ARGBLocalVideoFrameCallback,
                    ARGBRemoteVideoFrameCallback = CallbacksWrappers.ARGBRemoteVideoFrameCallback
                };

                // On UWP PeerConnectionCreate() fails on main UI thread, so always initialize the native peer
                // connection asynchronously from a background worker thread.
                //using (var cancelOrCloseToken = CancellationTokenSource.CreateLinkedTokenSource(_initCTS.Token, token))
                //{
                _initTask = Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();

                    var serversArray = servers.ToArray();
                    IntPtr nativeHandle = NativeMethods.PeerConnectionCreate(serversArray, serversArray.Length, username, credentials, video: false);
                    lock (_openCloseLock)
                    {
                        // The connection may have been aborted while being created, either via the
                        // cancellation token, or by calling Close() after the synchronous codepath
                        // above but before this task had a chance to run in the background.
                        if (token.IsCancellationRequested)
                        {
                            // Cancelled by token
                            NativeMethods.PeerConnectionClose(ref nativeHandle);
                            throw new OperationCanceledException(token);
                        }
                        if (!_selfHandle.IsAllocated)
                        {
                            // Cancelled by calling Close()
                            NativeMethods.PeerConnectionClose(ref nativeHandle);
                            throw new OperationCanceledException();
                        }

                        _nativePeerhandle = nativeHandle;

                        // Register all trampoline callbacks. Note that even passing a static managed method
                        // for the callback is not safe, because the compiler implicitly creates a delegate
                        // object (a static method is not a delegate itself; it can be wrapped inside one),
                        // and that delegate object will be garbage collected immediately at the end of this
                        // block. Instead, a delegate needs to be explicitly created and locked in memory.
                        // Since the current PeerConnection instance is already locked via _selfHandle,
                        // and it references all delegates via _peerCallbackArgs, those also can't be GC'd.
                        var self = GCHandle.ToIntPtr(_selfHandle);
                        NativeMethods.PeerConnectionRegisterConnectedCallback(
                            _nativePeerhandle, _peerCallbackArgs.ConnectedCallback, self);
                        NativeMethods.PeerConnectionRegisterLocalSdpReadytoSendCallback(
                            _nativePeerhandle, _peerCallbackArgs.LocalSdpReadytoSendCallback, self);
                        NativeMethods.PeerConnectionRegisterIceCandidateReadytoSendCallback(
                            _nativePeerhandle, _peerCallbackArgs.IceCandidateReadytoSendCallback, self);
                        NativeMethods.PeerConnectionRegisterRenegotiationNeededCallback(
                            _nativePeerhandle, _peerCallbackArgs.RenegotiationNeededCallback, self);
                        NativeMethods.PeerConnectionRegisterI420LocalVideoFrameCallback(
                            _nativePeerhandle, _peerCallbackArgs.I420LocalVideoFrameCallback, self);
                        NativeMethods.PeerConnectionRegisterI420RemoteVideoFrameCallback(
                            _nativePeerhandle, _peerCallbackArgs.I420RemoteVideoFrameCallback, self);
                        //NativeMethods.PeerConnectionRegisterARGBLocalVideoFrameCallback(
                        //    _nativePeerhandle, _peerCallbackArgs.ARGBLocalVideoFrameCallback, self);
                        //NativeMethods.PeerConnectionRegisterARGBRemoteVideoFrameCallback(
                        //    _nativePeerhandle, _peerCallbackArgs.ARGBRemoteVideoFrameCallback, self);
                    }
                }, token);

                return _initTask;
            }
        }

        /// <summary>
        /// Close the peer connection and destroy the underlying native resources.
        /// </summary>
        public void Close()
        {
            lock (_openCloseLock)
            {
                // If the connection is not initialized, return immediately.
                if (_initTask == null)
                {
                    return;
                }

                // Indicate to InitializeAsync() that it should stop returning _initTask,
                // as it is about to become invalid.
                _isClosing = true;
            }

            // Wait for any pending initializing to finish.
            // This must be outside of the lock because the initialization task will
            // eventually need to acquire the lock to complete.
            _initTask.Wait();

            lock (_openCloseLock)
            {
                _initTask = null;

                // This happens on connected connection only
                if (_nativePeerhandle != IntPtr.Zero)
                {
                    // Un-register all static trampoline callbacks
                    NativeMethods.PeerConnectionRegisterConnectedCallback(_nativePeerhandle, null, IntPtr.Zero);
                    NativeMethods.PeerConnectionRegisterLocalSdpReadytoSendCallback(_nativePeerhandle, null, IntPtr.Zero);
                    NativeMethods.PeerConnectionRegisterIceCandidateReadytoSendCallback(_nativePeerhandle, null, IntPtr.Zero);
                    NativeMethods.PeerConnectionRegisterRenegotiationNeededCallback(_nativePeerhandle, null, IntPtr.Zero);
                    NativeMethods.PeerConnectionRegisterI420LocalVideoFrameCallback(_nativePeerhandle, null, IntPtr.Zero);
                    NativeMethods.PeerConnectionRegisterI420RemoteVideoFrameCallback(_nativePeerhandle, null, IntPtr.Zero);
                    NativeMethods.PeerConnectionRegisterARGBLocalVideoFrameCallback(_nativePeerhandle, null, IntPtr.Zero);
                    NativeMethods.PeerConnectionRegisterARGBRemoteVideoFrameCallback(_nativePeerhandle, null, IntPtr.Zero);

                    // Close the native WebRTC peer connection and destroy the object
                    NativeMethods.PeerConnectionClose(ref _nativePeerhandle);
                    _nativePeerhandle = IntPtr.Zero;
                }

                // This happens on connected or connecting connection
                if (_selfHandle.IsAllocated)
                {
                    _peerCallbackArgs = null;
                    _selfHandle.Free();
                }

                _isClosing = false;
            }
        }

        /// <summary>
        /// Dispose of native resources by closing the peer connection.
        /// </summary>
        /// <remarks>This is equivalent to <see cref="Close"/>.</remarks>
        public void Dispose()
        {
            Close();
        }

        #endregion


        #region Local audio and video tracks

        /// <summary>
        /// Add to the current connection a video track from a local video capture device (webcam).
        /// </summary>
        /// <param name="device">Optional device to use, defaults to first available one.</param>
        /// <returns>Asynchronous task completed once the device is capturing and the track is added.</returns>
        /// <remarks>On UWP this requires the "webcam" capability.
        /// See <see cref="https://docs.microsoft.com/en-us/windows/uwp/packaging/app-capability-declarations"/>
        /// for more details.</remarks>
        /// <remarks>This method throws an exception if the peer connection is not initialized.</remarks>
        public Task AddLocalVideoTrackAsync(VideoCaptureDevice device = default(VideoCaptureDevice))
        {
            ThrowIfConnectionNotOpen();
            return Task.Run(() =>
            {
                // On UWP this cannot be called from the main UI thread, so always call it from
                // a background worker thread.
                if (!NativeMethods.PeerConnectionAddLocalVideoTrack(_nativePeerhandle, device.id))
                {
                    throw new Exception();
                }
            });
        }

        /// <summary>
        /// Remove from the current connection the local video track added with <see cref="AddLocalAudioTrackAsync"/>.
        /// </summary>
        /// <remarks>This method throws an exception if the peer connection is not initialized.</remarks>
        public void RemoveLocalVideoTrack()
        {
            ThrowIfConnectionNotOpen();
            NativeMethods.PeerConnectionRemoveLocalVideoTrack(_nativePeerhandle);
        }

        /// <summary>
        /// Add to the current connection an audio track from a local audio capture device (microphone).
        /// </summary>
        /// <returns>Asynchronous task completed once the device is capturing and the track is added.</returns>
        /// <remarks>On UWP this requires the "microphone" capability.
        /// See <see cref="https://docs.microsoft.com/en-us/windows/uwp/packaging/app-capability-declarations"/>
        /// for more details.</remarks>
        /// <remarks>This method throws an exception if the peer connection is not initialized.</remarks>
        public Task AddLocalAudioTrackAsync()
        {
            ThrowIfConnectionNotOpen();
            return Task.Run(() =>
            {
                // On UWP this cannot be called from the main UI thread, so always call it from
                // a background worker thread.
                if (!NativeMethods.PeerConnectionAddLocalAudioTrack(_nativePeerhandle))
                {
                    throw new Exception();
                }
            });
        }

        /// <summary>
        /// Remove from the current connection the local audio track added with <see cref="AddLocalAudioTrackAsync"/>.
        /// </summary>
        /// <remarks>This method throws an exception if the peer connection is not initialized.</remarks>
        public void RemoveLocalAudioTrack()
        {
            ThrowIfConnectionNotOpen();
            NativeMethods.PeerConnectionRemoveLocalAudioTrack(_nativePeerhandle);
        }

        #endregion


        #region Data tracks

        //public async Task<bool> AddDataChannel(WebRTCDataChannel dataChannel, string label, bool ordered, bool reliable)
        //{
        //    ThrowIfConnectionNotOpen();
        //    var args = new CallbacksWrappers.DataChannelCallbackArgs()
        //    {
        //        Peer = this,
        //        DataChannel = dataChannel,
        //        MessageCallback = CallbacksWrappers.DataChannelMessageCallback,
        //        BufferingCallback = CallbacksWrappers.DataChannelBufferingCallback,
        //        StateCallback = CallbacksWrappers.DataChannelStateCallback
        //    };
        //    var handle = GCHandle.Alloc(args, GCHandleType.Normal);
        //    IntPtr userData = GCHandle.ToIntPtr(handle);
        //    return await Task.Run(() =>
        //    {
        //        return NativeMethods.PeerConnectionAddDataChannel(_nativePeerhandle, -1, label, ordered, reliable,
        //        args.MessageCallback, userData, args.BufferingCallback, userData, args.StateCallback, userData);
        //    });
        //}

        //public async Task<bool> AddDataChannel(int id, WebRTCDataChannel dataChannel, string label, bool ordered, bool reliable)
        //{
        //    ThrowIfConnectionNotOpen();
        //    var args = new CallbacksWrappers.DataChannelCallbackArgs()
        //    {
        //        Peer = this,
        //        DataChannel = dataChannel,
        //        MessageCallback = CallbacksWrappers.DataChannelMessageCallback,
        //        BufferingCallback = CallbacksWrappers.DataChannelBufferingCallback,
        //        StateCallback = CallbacksWrappers.DataChannelStateCallback
        //    };
        //    var handle = GCHandle.Alloc(args, GCHandleType.Normal);
        //    IntPtr userData = GCHandle.ToIntPtr(handle);
        //    return await Task.Run(() => {
        //        return NativeMethods.PeerConnectionAddDataChannel( // needs to be called on signaling thread ideally
        //            _nativePeerhandle, id, label, ordered, reliable,
        //            args.MessageCallback, userData,
        //            args.BufferingCallback, userData,
        //            args.StateCallback, userData);
        //    });
        //}

        //public bool RemoveDataChannel(int id)
        //{
        //    ThrowIfConnectionNotOpen();
        //    return NativeMethods.PeerConnectionRemoveDataChannel(_nativePeerhandle, id);
        //}

        //public bool RemoveDataChannel(WebRTCDataChannel dataChannel)
        //{
        //    ThrowIfConnectionNotOpen();
        //    return NativeMethods.PeerConnectionRemoveDataChannel(_nativePeerhandle, dataChannel.Label);
        //}

        public void SendDataChannelMessage(int id, byte[] message)
        {
            ThrowIfConnectionNotOpen();
            NativeMethods.PeerConnectionSendDataChannelMessage(_nativePeerhandle, id, message, (ulong)message.LongLength);
        }

        #endregion


        #region Signaling

        /// <summary>
        /// Inform the WebRTC peer connection of a newly received ICE candidate.
        /// </summary>
        /// <param name="sdpMid"></param>
        /// <param name="sdpMlineindex"></param>
        /// <param name="candidate"></param>
        public void AddIceCandidate(string sdpMid, int sdpMlineindex, string candidate)
        {
            ThrowIfConnectionNotOpen();
            NativeMethods.PeerConnectionAddIceCandidate(_nativePeerhandle, sdpMid, sdpMlineindex, candidate);
        }

        public bool CreateOffer()
        {
            ThrowIfConnectionNotOpen();
            return NativeMethods.PeerConnectionCreateOffer(_nativePeerhandle);
        }

        public bool CreateAnswer()
        {
            ThrowIfConnectionNotOpen();
            return NativeMethods.PeerConnectionCreateAnswer(_nativePeerhandle);
        }

        public void SetRemoteDescription(string type, string sdp)
        {
            ThrowIfConnectionNotOpen();
            NativeMethods.PeerConnectionSetRemoteDescription(_nativePeerhandle, type, sdp);
        }

        #endregion


        /// <summary>
        /// Utility to throw an exception if a method is called before the underlying
        /// native peer connection has been initialized.
        /// </summary>
        private void ThrowIfConnectionNotOpen()
        {
            lock (_openCloseLock)
            {
                if (_nativePeerhandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Cannot invoke native method with invalid peer connection handle.");
                }
            }
        }

        /// <summary>
        /// Helper to convert between an <see cref="IntPtr"/> holding a <see cref="GCHandle"/> to
        /// a <see cref="PeerConnection"/> object and the object itself.
        /// </summary>
        /// <param name="userData">The <see cref="GCHandle"/> to the <see cref="PeerConnection"/> object,
        /// wrapped inside an <see cref="IntPtr"/>.</param>
        /// <returns>The corresponding <see cref="PeerConnection"/> object.</returns>
        private static PeerConnection FromIntPtr(IntPtr userData)
        {
            var peerHandle = GCHandle.FromIntPtr(userData);
            var peer = (peerHandle.Target as PeerConnection);
            return peer;
        }

        /// <summary>
        /// Collection of native P/Invoke static functions.
        /// </summary>
        private static class NativeMethods
        {
#if MR_SHARING_WIN
            private const string dllPath = "Microsoft.MixedReality.WebRTC.Native.dll";
#elif MR_SHARING_ANDROID
            private const string dllPath = "Microsoft.MixedReality.WebRTC.Native.so";
#endif


            #region Unmanaged delegates

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public delegate void VideoCaptureDeviceEnumCallback(string id, string name, IntPtr userData);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public delegate void VideoCaptureDeviceEnumCompletedCallback(IntPtr userData);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public delegate void PeerConnectionConnectedCallback(IntPtr userData);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public delegate void PeerConnectionLocalSdpReadytoSendCallback(IntPtr userData,
                string type, string sdp);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public delegate void PeerConnectionIceCandidateReadytoSendCallback(IntPtr userData,
                string candidate, int sdpMlineindex, string sdpMid);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public delegate void PeerConnectionRenegotiationNeededCallback(IntPtr userData);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public delegate void PeerConnectionI420VideoFrameCallback(IntPtr userData,
                IntPtr ydata, IntPtr udata, IntPtr vdata, IntPtr adata,
                int ystride, int ustride, int vstride, int astride,
                int frameWidth, int frameHeight);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public delegate void PeerConnectionARGBVideoFrameCallback(IntPtr userData,
                IntPtr data, int stride, int frameWidth, int frameHeight);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public delegate void PeerConnectionDataChannelMessageCallback(IntPtr userData, IntPtr data, ulong size);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public delegate void PeerConnectionDataChannelBufferingCallback(IntPtr userData,
                ulong previous, ulong current, ulong limit);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            public delegate void PeerConnectionDataChannelStateCallback(IntPtr userData, int state, int id);

            #endregion


            #region P/Invoke static functions

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsEnumVideoCaptureDevicesAsync")]
            public static extern void EnumVideoCaptureDevicesAsync(VideoCaptureDeviceEnumCallback callback, IntPtr userData,
                VideoCaptureDeviceEnumCompletedCallback completedCallback, IntPtr completedUserData);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionCreate")]
            public static extern IntPtr PeerConnectionCreate(string[] servers, int numServers, string username, string credentials, bool video);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionRegisterConnectedCallback")]
            public static extern void PeerConnectionRegisterConnectedCallback(IntPtr peerHandle,
                PeerConnectionConnectedCallback callback, IntPtr userData);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionRegisterLocalSdpReadytoSendCallback")]
            public static extern void PeerConnectionRegisterLocalSdpReadytoSendCallback(IntPtr peerHandle,
                PeerConnectionLocalSdpReadytoSendCallback callback, IntPtr userData);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionRegisterIceCandidateReadytoSendCallback")]
            public static extern void PeerConnectionRegisterIceCandidateReadytoSendCallback(IntPtr peerHandle,
                PeerConnectionIceCandidateReadytoSendCallback callback, IntPtr userData);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionRegisterRenegotiationNeededCallback")]
            public static extern void PeerConnectionRegisterRenegotiationNeededCallback(IntPtr peerHandle,
                PeerConnectionRenegotiationNeededCallback callback, IntPtr userData);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionRegisterI420LocalVideoFrameCallback")]
            public static extern void PeerConnectionRegisterI420LocalVideoFrameCallback(IntPtr peerHandle,
                PeerConnectionI420VideoFrameCallback callback, IntPtr userData);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionRegisterI420RemoteVideoFrameCallback")]
            public static extern void PeerConnectionRegisterI420RemoteVideoFrameCallback(IntPtr peerHandle,
                PeerConnectionI420VideoFrameCallback callback, IntPtr userData);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionRegisterARGBLocalVideoFrameCallback")]
            public static extern void PeerConnectionRegisterARGBLocalVideoFrameCallback(IntPtr peerHandle,
                PeerConnectionARGBVideoFrameCallback callback, IntPtr userData);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionRegisterARGBRemoteVideoFrameCallback")]
            public static extern void PeerConnectionRegisterARGBRemoteVideoFrameCallback(IntPtr peerHandle,
                PeerConnectionARGBVideoFrameCallback callback, IntPtr userData);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionAddLocalVideoTrack")]
            public static extern bool PeerConnectionAddLocalVideoTrack(IntPtr peerHandle, string videoDeviceId);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionAddLocalAudioTrack")]
            public static extern bool PeerConnectionAddLocalAudioTrack(IntPtr peerHandle);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionAddDataChannel")]
            public static extern bool PeerConnectionAddDataChannel(IntPtr peerHandle, int id, string label,
                bool ordered, bool reliable, PeerConnectionDataChannelMessageCallback messageCallback,
                IntPtr messageUserData, PeerConnectionDataChannelBufferingCallback bufferingCallback,
                IntPtr bufferingUserData, PeerConnectionDataChannelStateCallback stateCallback,
                IntPtr stateUserData);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionRemoveLocalVideoTrack")]
            public static extern void PeerConnectionRemoveLocalVideoTrack(IntPtr peerHandle);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionRemoveLocalAudioTrack")]
            public static extern void PeerConnectionRemoveLocalAudioTrack(IntPtr peerHandle);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionRemoveDataChannelById")]
            public static extern bool PeerConnectionRemoveDataChannel(IntPtr peerHandle, int id);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionRemoveDataChannelByLabel")]
            public static extern bool PeerConnectionRemoveDataChannel(IntPtr peerHandle, string label);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionSendDataChannelMessage")]
            public static extern bool PeerConnectionSendDataChannelMessage(IntPtr peerHandle, int id,
                byte[] data, ulong size);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionAddIceCandidate")]
            public static extern void PeerConnectionAddIceCandidate(IntPtr peerHandle, string sdpMid,
                int sdpMlineindex, string candidate);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionCreateOffer")]
            public static extern bool PeerConnectionCreateOffer(IntPtr peerHandle);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionCreateAnswer")]
            public static extern bool PeerConnectionCreateAnswer(IntPtr peerHandle);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionSetRemoteDescription")]
            public static extern void PeerConnectionSetRemoteDescription(IntPtr peerHandle, string type, string sdp);

            [DllImport(dllPath, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi,
                EntryPoint = "mrsPeerConnectionClose")]
            public static extern void PeerConnectionClose(ref IntPtr peerHandle);

            #endregion
        }

        /// <summary>
        /// Identifier for a video capture device.
        /// </summary>
        [Serializable]
        public struct VideoCaptureDevice
        {
            /// <summary>
            /// Unique device identifier.
            /// </summary>
            public string id;

            /// <summary>
            /// Friendly device name.
            /// </summary>
            public string name;
        }

        /// <summary>
        /// Get the list of available video capture devices.
        /// </summary>
        /// <returns>The list of available video capture devices.</returns>
        public static Task<List<VideoCaptureDevice>> GetVideoCaptureDevicesAsync()
        {
            var devices = new List<VideoCaptureDevice>();
            var eventWaitHandle = new EventWaitHandle(false, EventResetMode.ManualReset);
            var wrapper = new CallbacksWrappers.EnumVideoCaptureWrapper()
            {
                callback = (id, name) =>
                {
                    devices.Add(new VideoCaptureDevice() { id = id, name = name });
                },
                completedCallback = () =>
                {
                    // On enumeration end, signal the caller thread
                    eventWaitHandle.Set();
                }
            };
            IntPtr handle = GCHandle.ToIntPtr(GCHandle.Alloc(wrapper, GCHandleType.Normal));
            return Task.Run(() =>
            {
                // Execute the native async callback
                NativeMethods.EnumVideoCaptureDevicesAsync(CallbacksWrappers.VideoCaptureDeviceEnumCallback, handle,
                    CallbacksWrappers.VideoCaptureDeviceEnumCompletedCallback, handle);

                // Wait for end of enumerating
                eventWaitHandle.WaitOne();

                // Clean-up
                GCHandle.FromIntPtr(handle).Free();

                return devices;
            });
        }
    }
}
