using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.WebRTC;
using System.Text.RegularExpressions;

namespace Unity.RenderStreaming
{
    [Serializable]
    public class ButtonClickEvent : UnityEngine.Events.UnityEvent<int> { }

    [Serializable]
    public class ButtonClickElement
    {
        [Tooltip("Specifies the ID on the HTML")]
        public int elementId;
        public ButtonClickEvent click;
    }

    public class RenderStreaming : MonoBehaviour
    {
#pragma warning disable 0649
        [SerializeField, Tooltip("Address for signaling server")]
        private string urlSignaling = "http://localhost";

        [SerializeField, Tooltip("Array to set your own STUN/TURN servers")]
        private RTCIceServer[] iceServers = new RTCIceServer[]
        {
            new RTCIceServer()
            {
                urls = new string[] { "stun:stun.l.google.com:19302" }
            }
        };

        [SerializeField, Tooltip("Streaming size should match display aspect ratio")]
        private Vector2Int streamingSize = new Vector2Int(1280, 720);

        [SerializeField, Tooltip("Streaming bit rate")]
        private int bitrate = 1000000;

        [SerializeField, Tooltip("Time interval for polling from signaling server")]
        private float interval = 5.0f;

        [SerializeField, Tooltip("Camera to capture video stream")]
        private Camera[] captureCameras;

        [SerializeField, Tooltip("Enable or disable hardware encoder")]
        private bool hardwareEncoderSupport = true;

        [SerializeField, Tooltip("Array to set your own click event")]
        private ButtonClickElement[] arrayButtonClickEvent;
#pragma warning restore 0649

        private Signaling signaling;
        private Dictionary<string, RTCPeerConnection> pcs = new Dictionary<string, RTCPeerConnection>();
        private Dictionary<RTCPeerConnection, Dictionary<int, RTCDataChannel>> mapChannels = new Dictionary<RTCPeerConnection, Dictionary<int, RTCDataChannel>>();
        private RTCConfiguration conf;
        private string sessionId;
        private MediaStream audioStream;
        private MediaStream videoStream;
        private List<VideoStreamTrack> videoTracks = new List<VideoStreamTrack>();

        public void Awake()
        {
            var encoderType = hardwareEncoderSupport ? EncoderType.Hardware : EncoderType.Software;
            WebRTC.WebRTC.Initialize(encoderType);
            RemoteInput.Initialize();
            RemoteInput.ActionButtonClick = OnButtonClick;
        }

        public void OnDestroy()
        {
            WebRTC.WebRTC.Dispose();
            RemoteInput.Destroy();
            Unity.WebRTC.Audio.Stop();
        }

        public IEnumerator Start()
        {
            videoStream = new MediaStream();
            foreach (var _camera in captureCameras)
            {
                var track = _camera.CaptureStreamTrack(streamingSize.x, streamingSize.y, bitrate);
                videoTracks.Add(track);
                videoStream.AddTrack(track);
            }
            audioStream = WebRTC.Audio.CaptureStream();

            signaling = new Signaling(urlSignaling);
            var opCreate = signaling.Create();
            yield return opCreate;
            if (opCreate.webRequest.isNetworkError)
            {
                Debug.LogError($"Network Error: {opCreate.webRequest.error}");
                yield break;
            }
            var newResData = opCreate.webRequest.DownloadHandlerJson<NewResData>().GetObject();
            sessionId = newResData.sessionId;

            conf = default;
            conf.iceServers = iceServers;
            StartCoroutine(WebRTC.WebRTC.Update());
            StartCoroutine(LoopPolling());
        }

        public Vector2Int GetStreamingSize() { return streamingSize; }

        long lastTimeGetOfferRequest;
        long lastTimeGetCandidateRequest;

        IEnumerator LoopPolling()
        {
            // ignore messages arrived before 30 secs ago
            lastTimeGetOfferRequest = DateTime.UtcNow.ToJsMilliseconds() - 30000;
            lastTimeGetCandidateRequest = DateTime.UtcNow.ToJsMilliseconds() - 30000;

            while (true)
            {
                yield return StartCoroutine(GetOffer());
                yield return StartCoroutine(GetCandidate());
                yield return new WaitForSeconds(interval);
            }
        }

        IEnumerator GetOffer()
        {
            var op = signaling.GetOffer(sessionId, lastTimeGetOfferRequest);
            yield return op;
            if (op.webRequest.isNetworkError)
            {
                Debug.LogError($"Network Error: {op.webRequest.error}");
                yield break;
            }
            var date = DateTimeExtension.ParseHttpDate(op.webRequest.GetResponseHeader("Date"));
            lastTimeGetOfferRequest = date.ToJsMilliseconds();

            var obj = op.webRequest.DownloadHandlerJson<OfferResDataList>().GetObject();
            if (obj == null)
            {
                yield break;
            }
            foreach (var offer in obj.offers)
            {
                RTCSessionDescription _desc;
                _desc.type = RTCSdpType.Offer;
                _desc.sdp = offer.sdp;
                var connectionId = offer.connectionId;
                if (pcs.ContainsKey(connectionId))
                {
                    continue;
                }
                var pc = new RTCPeerConnection();
                pcs.Add(offer.connectionId, pc);

                pc.OnDataChannel = channel => { OnDataChannel(pc, channel); };
                pc.SetConfiguration(ref conf);
                pc.OnIceCandidate = candidate => { StartCoroutine(OnIceCandidate(offer.connectionId, candidate)); };
                pc.OnIceConnectionChange = state =>
                {
                    if(state == RTCIceConnectionState.Disconnected)
                    {
                        pc.Close();
                    }
                };

                foreach (var track in videoStream.GetTracks())
                {
                    Debug.Log("add video track id" + track.Id);
                    pc.AddTrack(track, videoStream);
                    //pc.AddTransceiver(track, videoStream, RTCRtpTransceiverDirection.SendOnly);
                }
                Debug.Log("videoStream id=" + videoStream.Id);
                Debug.Log("videoStream tracks count=" + videoStream.GetVideoTracks().Count());

                foreach (var track in audioStream.GetTracks())
                {
                    pc.AddTrack(track);
                }

                //make video bit rate starts at 16000kbits, and 160000kbits at max.
                string pattern = @"(a=fmtp:\d+ .*level-asymmetry-allowed=.*)\r\n";
                _desc.sdp = Regex.Replace(_desc.sdp, pattern, "$1;x-google-start-bitrate=16000;x-google-max-bitrate=160000\r\n");
                var op2 = pc.SetRemoteDescription(ref _desc);
                yield return op2;
                RTCAnswerOptions options = default;
                var _op = pc.CreateAnswer(ref options);
                yield return _op;
                /*
                if (op.IsError)
                {
                    Debug.LogError($"Network Error: {op.Error}");
                    yield break;
                }
                */

                var desc = _op.Desc;
                Debug.Log("answer sdp video count=" + desc.sdp.Split('\n').Count(line => line.Contains("m=video")));


                StartCoroutine(Answer(connectionId));
            }
        }

        IEnumerator Answer(string connectionId)
        {
            var pc = pcs[connectionId];

            RTCOfferOptions _options = default;
            var _op = pc.CreateOffer(ref _options);
            yield return _op;
            Debug.Log("sdp video count=" + _op.Desc.sdp.Split('\n').Count(line => line.Contains("m=video")));


            RTCAnswerOptions options = default;
            var op = pc.CreateAnswer(ref options);
            yield return op;
            if (op.IsError)
            {
                Debug.LogError($"Network Error: {op.Error}");
                yield break;
            }

            var desc = op.Desc;
            Debug.Log("sdp video count=" +desc.sdp.Split('\n').Count(line => line.Contains("m=video")));
            var opLocalDesc = pc.SetLocalDescription(ref desc);
            yield return opLocalDesc;
            if (opLocalDesc.IsError)
            {
                Debug.LogError($"Network Error: {opLocalDesc.Error}");
                yield break;
            }
            var op3 = signaling.PostAnswer(this.sessionId, connectionId, op.Desc.sdp);
            Debug.Log(op.Desc.sdp);
            yield return op3;
            if (op3.webRequest.isNetworkError)
            {
                Debug.LogError($"Network Error: {op3.webRequest.error}");
            }
        }

        IEnumerator GetCandidate()
        {
            var op = signaling.GetCandidate(sessionId, lastTimeGetCandidateRequest);
            yield return op;

            if (op.webRequest.isNetworkError)
            {
                Debug.LogError($"Network Error: {op.webRequest.error}");
                yield break;
            }
            var date = DateTimeExtension.ParseHttpDate(op.webRequest.GetResponseHeader("Date"));
            lastTimeGetCandidateRequest = date.ToJsMilliseconds();

            var obj = op.webRequest.DownloadHandlerJson<CandidateContainerResDataList>().GetObject();
            if (obj == null)
            {
                yield break;
            }
            foreach (var candidateContainer in obj.candidates)
            {
                RTCPeerConnection pc;
                if (!pcs.TryGetValue(candidateContainer.connectionId, out pc))
                {
                    continue;
                }
                foreach (var candidate in candidateContainer.candidates)
                {
                    RTCIceCandidate​ _candidate = default;
                    _candidate.candidate = candidate.candidate;
                    _candidate.sdpMLineIndex = candidate.sdpMLineIndex;
                    _candidate.sdpMid = candidate.sdpMid;

                    pcs[candidateContainer.connectionId].AddIceCandidate(ref _candidate);
                }
            }
        }

        IEnumerator OnIceCandidate(string connectionId, RTCIceCandidate​ candidate)
        {
            var opCandidate = signaling.PostCandidate(sessionId, connectionId, candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex);
            yield return opCandidate;
            if (opCandidate.webRequest.isNetworkError)
            {
                Debug.LogError($"Network Error: {opCandidate.webRequest.error}");
            }
        }
        void OnDataChannel(RTCPeerConnection pc, RTCDataChannel channel)
        {
            Dictionary<int, RTCDataChannel> channels;
            if (!mapChannels.TryGetValue(pc, out channels))
            {
                channels = new Dictionary<int, RTCDataChannel>();
                mapChannels.Add(pc, channels);
            }
            channels.Add(channel.Id, channel);

            if(channel.Label == "data")
            {
                channel.OnMessage = bytes => { RemoteInput.ProcessInput(bytes); };
                channel.OnClose = () => { RemoteInput.Reset(); };
            }
        }

        void OnButtonClick(int elementId)
        {
            foreach (var element in arrayButtonClickEvent)
            {
                if (element.elementId == elementId)
                {
                    element.click.Invoke(elementId);
                }
            }
        }
    }
}
