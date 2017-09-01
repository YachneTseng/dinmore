using Dinmore.Uwp.Infrastructure.Media;
using Dinmore.Uwp.Models;
using Microsoft.Bot.Connector.DirectLine;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.FaceAnalysis;
using Windows.Media.MediaProperties;
using Windows.Media.SpeechRecognition;
using Windows.Networking.Connectivity;
using Windows.Storage;
using Windows.System.Threading;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using ZXing;

namespace Dinmore.Uwp
{
    /// <summary>
    /// Page for demonstrating FaceTracking.
    /// </summary>
    public sealed partial class WebcamFaceDetector : Page
    {
        /// <summary>
        /// Brush for drawing the bounding box around each identified face.
        /// </summary>
        private readonly SolidColorBrush lineBrush = new SolidColorBrush(Windows.UI.Colors.Yellow);

        /// <summary>
        /// Thickness of the face bounding box lines.
        /// </summary>
        private readonly double lineThickness = 2.0;

        /// <summary>
        /// Transparent fill for the bounding box.
        /// </summary>
        private readonly SolidColorBrush fillBrush = new SolidColorBrush(Windows.UI.Colors.Transparent);

        /// <summary>
        /// References a MediaCapture instance; is null when not in Streaming state.
        /// </summary>
        private MediaCapture mediaCapture;

        /// <summary>
        /// Cache of properties from the current MediaCapture device which is used for capturing the preview frame.
        /// </summary>
        private VideoEncodingProperties videoProperties;

        /// <summary>
        /// References a FaceTracker instance.
        /// </summary>
        private FaceTracker faceTracker;

        /// <summary>
        /// A periodic timer to execute FaceTracker on preview frames
        /// </summary>
        private ThreadPoolTimer frameProcessingTimer;

        /// <summary>
        /// Semaphore to ensure FaceTracking logic only executes one at a time
        /// </summary>
        private SemaphoreSlim frameProcessingSemaphore = new SemaphoreSlim(1);

        /// <summary>
        /// Detect any speech to the app
        /// </summary>
        private SpeechRecognizer speechRecognizer;

        /// <summary>
        /// Speech needs enabling within settings
        /// </summary>
        private static uint HResultPrivacyStatementDeclined = 0x80045509;

        private double ApiIntervalMs;
        private int NumberMilliSecsForFacesToDisappear;
        private int NumberMilliSecsToWaitForHello;
        private int NumberMillSecsBeforeWePlayAgain;
        private TimeSpan timerInterval;

        /// <summary>
        /// The current step of the state machine for detecting faces, playing sounds etc.
        /// </summary>
        public DetectionState CurrentState { get; set; }

        public ObservableCollection<StatusMessage> StatusLog { get; set; } = new ObservableCollection<StatusMessage>();

        private static ResourceLoader AppSettings;

        private IVoicePlayer vp = new VoicePlayerGenerated();

        private VoicePlayerGenerated vpGenerated = new VoicePlayerGenerated();

        private const string _DeviceIdKey = "DeviceId";

        /// <summary>
        /// Initializes a new instance of the <see cref="WebcamFaceDetector"/> class.
        /// </summary>
        public WebcamFaceDetector()
        {
            //Defaults
            AppSettings = ResourceLoader.GetForCurrentView();
            NumberMilliSecsForFacesToDisappear =
                int.Parse(AppSettings.GetString("NumberMilliSecsForFacesToDisappear"));
            NumberMilliSecsToWaitForHello =
                int.Parse(AppSettings.GetString("NumberMilliSecsToWaitForHello"));
            NumberMillSecsBeforeWePlayAgain =
                int.Parse(AppSettings.GetString("NumberMillSecsBeforeWePlayAgain"));

            var timerIntervalMilliSecs =
                int.Parse(AppSettings.GetString("TimerIntervalMilliSecs"));
            timerInterval = TimeSpan.FromMilliseconds(timerIntervalMilliSecs);

            ApiIntervalMs =
                double.Parse(AppSettings.GetString("ApiIntervalMilliSecs"));


            InitializeComponent();

            CurrentState = new DetectionState { State = DetectionStates.Idle };
            App.Current.Suspending += OnSuspending;
        }

        /// <summary>
        /// Responds when we navigate to this page.
        /// </summary>
        /// <param name="e">Event data</param>
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {

            // The 'await' operation can only be used from within an async method but class constructors
            // cannot be labeled as async, and so we'll initialize FaceTracker here.
            if (faceTracker == null)
            {
                faceTracker = await FaceTracker.CreateAsync();
                ChangeDetectionState(DetectionStates.Startup);
            }

            // Prompt for permission to access the microphone. This request will only happen
            // once, it will not re-prompt if the user rejects the permission.
            if (await AudioCapturePermissions.RequestMicrophonePermission())
            {
                await StartSpeechRecognition();
            }
            else
            {
                Say(AppSettings.GetString("MicrophonePrivacyDeclined"));
            }
        }

        private string GetLocalIp()
        {
            // TODO: Make this more robust https://github.com/blackradley/dinmore/issues/29
            try
            {
                var icp = NetworkInformation.GetInternetConnectionProfile();

                if (icp?.NetworkAdapter == null) return null;
                var hostname =
                    NetworkInformation.GetHostNames()
                        .SingleOrDefault(
                            hn =>
                                hn.IPInformation?.NetworkAdapter != null && hn.IPInformation.NetworkAdapter.NetworkAdapterId
                                == icp.NetworkAdapter.NetworkAdapterId);

                // the ip address
                return hostname?.CanonicalName;
            }
            catch
            {
                return "Greedy, 2 network cards";
            }
        }

        /// <summary>
        /// Responds to App Suspend event to stop/release MediaCapture object if it's running and return to Idle state.
        /// </summary>
        /// <param name="sender">The source of the Suspending event</param>
        /// <param name="e">Event data</param>
        private void OnSuspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            if (CurrentState.State == DetectionStates.Startup || CurrentState.State == DetectionStates.WaitingForFaces)
            {
                var deferral = e.SuspendingOperation.GetDeferral();
                try
                {
                    ChangeDetectionState(DetectionStates.Idle);
                }
                finally
                {
                    deferral.Complete();
                }
            }
        }

        /// <summary>
        /// Get a given camera id if provided
        /// </summary>
        /// <returns></returns>
        private async Task<DeviceInformation> GetDesiredWebcameraDeviceAsync()
        {
            // Finds all video capture devices
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            DeviceInformation desiredDevice = devices.FirstOrDefault(x => x.Name.Equals(AppSettings.GetString("CameraDeviceName")));
            return desiredDevice ?? devices.FirstOrDefault();
        }

        /// <summary>
        /// Initializes a new MediaCapture instance and starts the Preview streaming to the CamPreview UI element.
        /// </summary>
        /// <returns>Async Task object returning true if initialization and streaming were successful and false if an exception occurred.</returns>
        private async Task<bool> StartWebcamStreaming()
        {
            // Speak the IP Out loud
            Say($"The IP Address is: {GetLocalIp()}");


            bool successful = true;

            try
            {

                mediaCapture = new MediaCapture();

                // For this scenario, we only need Video (not microphone) so specify this in the initializer.
                // NOTE: the appxmanifest only declares "webcam" under capabilities and if this is changed to include
                // microphone (default constructor) you must add "microphone" to the manifest or initialization will fail.

                var device = await GetDesiredWebcameraDeviceAsync();
                var settings = new MediaCaptureInitializationSettings { VideoDeviceId = device.Id, StreamingCaptureMode = StreamingCaptureMode.Video };
                await mediaCapture.InitializeAsync(settings);
                mediaCapture.Failed += MediaCapture_CameraStreamFailed;

                // Cache the media properties as we'll need them later.
                var deviceController = mediaCapture.VideoDeviceController;
                videoProperties = deviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;

                // Immediately start streaming to our CaptureElement UI.
                // NOTE: CaptureElement's Source must be set before streaming is started.
                CamPreview.Source = mediaCapture;
                await mediaCapture.StartPreviewAsync();

                RunTimer();

            }
            catch (UnauthorizedAccessException)
            {
                // There is now webcam present. Please Intall One.

                Say("There is no webcam present, please add a USB webcam and restart the exhibit");

                // If the user has disabled their webcam this exception is thrown; provide a descriptive message to inform the user of this fact.
                //LogStatusMessage("Webcam is disabled or access to the webcam is disabled for this app.\nEnsure Privacy Settings allow webcam usage.", StatusSeverity.Error);
                successful = false;

            }
            catch (Exception ex)
            {
                Say("There is no webcam present, please add a USB webcam and restart the exhibit");
                //LogStatusMessage("Unable to start camera: " + ex.ToString(), StatusSeverity.Error);
                successful = false;
            }

            return successful;
        }

        private async Task StartSpeechRecognition()
        {
            // Create an instance of SpeechRecognizer.
            speechRecognizer = new SpeechRecognizer();
            speechRecognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromDays(1);

            // Compile the dictation grammar by default.
            var speechRecogState = await speechRecognizer.CompileConstraintsAsync();
            if (speechRecogState.Status != SpeechRecognitionResultStatus.Success)
            {
                Debug.WriteLine($"StartSpeechRecognition: {speechRecogState.Status}");
                LogStatusMessage($"Error starting speech recognition: {speechRecogState.Status}", StatusSeverity.Error);
                return;
            }

            // Hook up event handlers
            speechRecognizer.StateChanged += SpeechRecognizer_StateChanged;
            speechRecognizer.ContinuousRecognitionSession.Completed += ContinuousRecognitionSession_Completed;
            speechRecognizer.ContinuousRecognitionSession.ResultGenerated += ContinuousRecognitionSession_ResultGenerated;

            try
            {
                // The recognizer can only start listening in a continuous fashion if the recognizer is currently idle.
                // This prevents an exception from occurring.
                if (speechRecognizer.State == SpeechRecognizerState.Idle)
                {
                    await speechRecognizer.ContinuousRecognitionSession.StartAsync();
                }
                else
                {
                    Debug.WriteLine($"StartSpeechRecognition speechRecognizer.State: {speechRecognizer.State}");
                    LogStatusMessage($"Error starting speech recognition speechRecognizer.State: {speechRecognizer.State}", StatusSeverity.Error);
                    return;
                }
            }
            catch (Exception exp)
            {
                Debug.WriteLine($"StartSpeechRecognition: {exp}");
                // Handle the speech privacy policy error.
                if ((uint)exp.HResult == HResultPrivacyStatementDeclined)
                {
                    LogStatusMessage(AppSettings.GetString("PrivacyStatementDeclined"), StatusSeverity.Error);
                    //await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-accounts"));
                }
                else
                {
                    LogStatusMessage(exp.Message, StatusSeverity.Error);
                }
            }
        }

        private async Task EndSpeechRecognition()
        {
            try
            {
                await speechRecognizer.StopRecognitionAsync();
            }
            catch (Exception exp)
            {
                // eat
            }

            // Unhook events
            speechRecognizer.StateChanged -= SpeechRecognizer_StateChanged;
            speechRecognizer.ContinuousRecognitionSession.Completed -= ContinuousRecognitionSession_Completed;
            speechRecognizer.ContinuousRecognitionSession.ResultGenerated -= ContinuousRecognitionSession_ResultGenerated;
            speechRecognizer.Dispose();
        }

        /// <summary>
        /// Process any speech detected and send to the bot framework to in turn query the QnA Maker database
        /// Essentially here we have performed SST, the text is sent via direct line to the bot framework
        /// Once a text response is recieved we convert this back to speech using TTS
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private async void ContinuousRecognitionSession_ResultGenerated(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
        {
            // Check we're confident with the results of SST
            if (args.Result.Confidence == SpeechRecognitionConfidence.Medium || args.Result.Confidence == SpeechRecognitionConfidence.High)
            {
                Debug.WriteLine($"ContinuousRecognitionSession_ResultGenerated: {args.Result.Text}");
                LogStatusMessage($"ContinuousRecognitionSession_ResultGenerated: {args.Result.Text}", StatusSeverity.Info);

                try
                {
                    // Send the message to the bot and get a response
                    string conversationId = await PostMessageToApiAsync(args.Result.Text);
                    if (conversationId == null)
                    {
                        Debug.WriteLine($"ContinuousRecognitionSession_ResultGenerated: No conversation Id returned from bot");
                        LogStatusMessage($"ContinuousRecognitionSession_ResultGenerated: No conversation Id returned from bot", StatusSeverity.Info);
                    }

                    var botResponse = await GetMessageToApiAsync(conversationId);
                    if (botResponse != null)
                    {
                        Debug.WriteLine($"ContinuousRecognitionSession_ResultGenerated: Bot response {botResponse}");
                        LogStatusMessage($"ContinuousRecognitionSession_ResultGenerated: Bot response {botResponse}", StatusSeverity.Info);
                        Say(botResponse);
                    }
                }
                catch (Exception exp)
                {
                    // Eat this
                    Debug.WriteLine($"ContinuousRecognitionSession_ResultGenerated: {exp}");
                    LogStatusMessage($"ContinuousRecognitionSession_ResultGenerated: {exp}", StatusSeverity.Error);
                }
            }
            else
            {
                Debug.WriteLine($"ContinuousRecognitionSession_ResultGenerated: {args.Result.Text}");
                LogStatusMessage($"ContinuousRecognitionSession_ResultGenerated: {args.Result.Confidence} Detected: {args.Result.Text}", StatusSeverity.Info);

                // In some scenarios, a developer may choose to ignore giving the user feedback in this case, if speech
                // is not the primary input mechanism for the application.
                Say("I didn't understand you, please rephrase your question");
            }
        }

        private async void ContinuousRecognitionSession_Completed(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args)
        {
            LogStatusMessage($"SpeechRecognition ended: {args.Status}", StatusSeverity.Warning);

            // Clean up and start over
            await EndSpeechRecognition();
            await StartSpeechRecognition();
        }

        private void SpeechRecognizer_StateChanged(SpeechRecognizer sender, SpeechRecognizerStateChangedEventArgs args)
        {
            LogStatusMessage($"SpeechRecognition state changed: {args.State}", StatusSeverity.Warning);
        }

        private void Say(string phrase)
        {
            vpGenerated.Say(phrase);
        }

        private void RunTimer()
        {
            frameProcessingTimer = ThreadPoolTimer.CreateTimer(
                new TimerElapsedHandler(ProcessCurrentStateAsync), timerInterval);
        }

        /// <summary>
        /// Safely stops webcam streaming (if running) and releases MediaCapture object.
        /// </summary>
        private async void ShutdownWebCam()
        {
            if (frameProcessingTimer != null)
            {
                frameProcessingTimer.Cancel();
            }

            if (mediaCapture != null)
            {
                if (mediaCapture.CameraStreamState == Windows.Media.Devices.CameraStreamState.Streaming)
                {
                    try
                    {
                        await mediaCapture.StopPreviewAsync();
                    }
                    catch (Exception)
                    {
                        ;   // Since we're going to destroy the MediaCapture object there's nothing to do here
                    }
                }
                mediaCapture.Dispose();
            }

            frameProcessingTimer = null;
            CamPreview.Source = null;
            mediaCapture = null;
        }

        private async void ProcessCurrentStateAsync(ThreadPoolTimer timer)
        {
            try
            {
                switch (CurrentState.State)
                {
                    case DetectionStates.Idle:
                        break;

                    case DetectionStates.Startup:

                        break;

                    case DetectionStates.OnBoarding:
                        var result = await ProcessCurrentVideoFrameForQRCodeAsync();
                        //if we now have a GUID, store it and then change the state
                        if (!string.IsNullOrEmpty(result))
                        {
                            //store the device id guid
                            ApplicationData.Current.LocalSettings.Values[_DeviceIdKey] = result;

                            LogStatusMessage($"Found a QR code with device id {result} which has been stored to app storage.", StatusSeverity.Info);

                            Say("I found a QR code, thanks.");

                            ChangeDetectionState(DetectionStates.WaitingForFaces);
                        }
                        break;

                    case DetectionStates.WaitingForFaces:
                        //LogStatusMessage("Waiting for faces", StatusSeverity.Info);
                        CurrentState.ApiRequestParameters = await ProcessCurrentVideoFrameAsync();

                        if (CurrentState.ApiRequestParameters != null)
                        {
                            ChangeDetectionState(DetectionStates.FaceDetectedOnDevice);
                        }
                        break;

                    case DetectionStates.FaceDetectedOnDevice:
                        //LogStatusMessage("Just about to send API call for faces", StatusSeverity.Info);

                        //Should we play? MORE DESC REQUIRED
                        if (CurrentState.LastImageApiPush.AddMilliseconds(ApiIntervalMs) < DateTimeOffset.UtcNow
                            && CurrentState.TimeVideoWasStopped.AddMilliseconds(NumberMillSecsBeforeWePlayAgain) < DateTimeOffset.UtcNow)
                        {
                            //ThreadPoolTimer.CreateTimer(
                            //    new TimerElapsedHandler(HelloAudioHandler),
                            //    TimeSpan.FromMilliseconds(NumberMilliSecsToWaitForHello));

                            HelloAudio();

                            CurrentState.LastImageApiPush = DateTimeOffset.UtcNow;
                            CurrentState.FacesFoundByApi = await PostImageToApiAsync(CurrentState.ApiRequestParameters.Image);

                            LogStatusMessage($"Sending faces to api", StatusSeverity.Info);

                            ChangeDetectionState(DetectionStates.ApiResponseReceived);
                        }
                        break;

                    case DetectionStates.ApiResponseReceived:
                        //LogStatusMessage("API response received", StatusSeverity.Info);

                        if (CurrentState.FacesFoundByApi != null && CurrentState.FacesFoundByApi.Any())
                        {
                            LogStatusMessage("Face(s) detected", StatusSeverity.Info);
                            ChangeDetectionState(DetectionStates.InterpretingApiResults);
                            CurrentState.FacesStillPresent = true;

                            break;
                        }
                        //ChangeDetectionState(DetectionStates.WaitingForFaces);
                        ChangeDetectionState(DetectionStates.WaitingForFacesToDisappear);
                        break;

                    case DetectionStates.InterpretingApiResults:
                        // We have faces and data, so decide what to do here (play a sound etc).
                        // You'd probably kick this off in a background thread and track it by putting a
                        // reference into the CurrentState object (new property).

                        //play media if we are not currently playing
                        CurrentState.FacesStillPresent = true;

                        if (!vp.IsCurrentlyPlaying)
                        {
                            LogStatusMessage("Starting playlist", StatusSeverity.Info);

                            var play = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                            {
                                //TODO This needs 
                                //vp.Play(CurrentState);

                                //conversation = await directLine.Conversations.StartConversationAsync();
                                //await StartSpeechRecognition();
                            });
                        }

                        // Check here if the media has finished playing or the people have walked away.
                        //ChangeDetectionState(DetectionStates.WaitingForFaces);
                        ChangeDetectionState(DetectionStates.WaitingForFacesToDisappear);

                        break;

                    //Some faces are on the device and the api has been called, and maybe the audio
                    //  is now playing
                    case DetectionStates.WaitingForFacesToDisappear:

                        CurrentState.FacesStillPresent = await AreFacesStillPresent();
                        LogStatusMessage($"Faces present: {CurrentState.FacesStillPresent}", StatusSeverity.Info);

                        //we dont have a face
                        if (!CurrentState.FacesStillPresent)
                        {
                            //TODO Refactor this out.
                            await Task.Delay(NumberMilliSecsForFacesToDisappear)
                                .ContinueWith((t =>
                                {
                                    CurrentState.FacesStillPresent = AreFacesStillPresent().Result;
                                    if (!CurrentState.FacesStillPresent)
                                    {
                                        LogStatusMessage($"Faces have gone for a few or more secs, stop the audio playback", StatusSeverity.Info);
                                        ChangeDetectionState(DetectionStates.WaitingForFaces);
                                        vp.Stop();
                                        CurrentState.TimeVideoWasStopped = DateTimeOffset.UtcNow;
                                        return;
                                    }
                                }
                                ));

                        }


                        break;

                    default:
                        ChangeDetectionState(DetectionStates.Idle);
                        break;
                }
            }
            catch (Exception ex)
            {
                LogStatusMessage("Unable to process current frame. " + ex.ToString(), StatusSeverity.Error);
            }
            finally
            {
                RunTimer();
            }
        }

        private void HelloAudio()
        {
            LogStatusMessage("Starting introduction", StatusSeverity.Info);

            vp.PlayIntroduction(CurrentState.ApiRequestParameters.Faces.Count());
            //timer.Cancel();
        }


        private void LogStatusMessage(string message, StatusSeverity severity)
        {
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                while (StatusLog.Count > 100)
                {
                    StatusLog.RemoveAt(StatusLog.Count - 1);
                }

                StatusLog.Insert(0, new StatusMessage(message, severity));
            });
        }

        private async Task<List<Face>> PostImageToApiAsync(byte[] image)
        {
            try
            {
                using (var httpClient = new HttpClient())
                {
                    var content = new StreamContent(new MemoryStream(image));
                    content.Headers.ContentType = new MediaTypeWithQualityHeaderValue("application/octet-stream");

                    //build url to pass to api, REFACTORING NEEDED
                    var url = AppSettings.GetString("FaceApiUrl");
                    var deviceId = ApplicationData.Current.LocalSettings.Values[_DeviceIdKey];
                    url = $"{url}?deviceid={deviceId}";

                    var responseMessage = await httpClient.PostAsync(url, content);

                    if (!responseMessage.IsSuccessStatusCode)
                    {
                        switch (responseMessage.StatusCode.ToString())
                        {
                            case "BadRequest":
                                LogStatusMessage("The API returned a 400 Bad Request. This is caused by either a missing DeviceId parameter or one containig a GUID that is not already registered with the device API.", StatusSeverity.Error);
                                break;
                            default:
                                LogStatusMessage($"The API returned a non-sucess status {responseMessage.ReasonPhrase}", StatusSeverity.Error);
                                break;
                        }
                        return null;
                    }

                    var response = await responseMessage.Content.ReadAsStringAsync();
                    var result = JsonConvert.DeserializeObject<List<Face>>(response);

                    return result;
                }
            }
            catch (Exception ex)
            {
                vp.IsCurrentlyPlaying = false;
                LogStatusMessage("Exception: " + ex.ToString(), StatusSeverity.Error);
                return null;
            }
        }

        private async Task<string> PostMessageToApiAsync(string messageText)
        {
            try
            {
                using (var httpClient = new HttpClient())
                {
                    var content = new StringContent(messageText);

                    var url = AppSettings.GetString("BotApiUrl");
                    var deviceId = ApplicationData.Current.LocalSettings.Values[_DeviceIdKey];
                    url = $"{url}?deviceid={deviceId}&message={messageText}";

                    var responseMessage = await httpClient.PostAsync(url, content);

                    if (!responseMessage.IsSuccessStatusCode)
                    {
                        switch (responseMessage.StatusCode.ToString())
                        {
                            case "BadRequest":
                                LogStatusMessage("The API returned a 400 Bad Request. This is caused by either a missing DeviceId parameter or one containig a GUID that is not already registered with the device API.", StatusSeverity.Error);
                                break;
                            default:
                                LogStatusMessage($"The API returned a non-sucess status {responseMessage.ReasonPhrase}", StatusSeverity.Error);
                                break;
                        }
                        return null;
                    }

                    var response = await responseMessage.Content.ReadAsStringAsync();
                    return response;
                    //JObject s = JObject.Parse(response);
                    //return s["id"].ToString();
                }
            }
            catch (Exception ex)
            {
                LogStatusMessage("Exception: " + ex.ToString(), StatusSeverity.Error);
                return null;
            }
        }

        private async Task<string> GetMessageToApiAsync(string conversationId)
        {
            try
            {
                using (var httpClient = new HttpClient())
                {
                    var content = new StringContent(conversationId);

                    var url = AppSettings.GetString("BotApiUrl");
                    var deviceId = ApplicationData.Current.LocalSettings.Values[_DeviceIdKey];
                    url = $"{url}?conversationId={conversationId}";

                    var responseMessage = await httpClient.GetAsync(url);

                    if (!responseMessage.IsSuccessStatusCode)
                    {
                        switch (responseMessage.StatusCode.ToString())
                        {
                            case "BadRequest":
                                LogStatusMessage("The API returned a 400 Bad Request. This is caused by either a missing DeviceId parameter or one containig a GUID that is not already registered with the device API.", StatusSeverity.Error);
                                break;
                            default:
                                LogStatusMessage($"The API returned a non-sucess status {responseMessage.ReasonPhrase}", StatusSeverity.Error);
                                break;
                        }
                        return null;
                    }

                    var response = await responseMessage.Content.ReadAsStringAsync();
                    return response;
                }
            }
            catch (Exception ex)
            {
                vp.IsCurrentlyPlaying = false;
                LogStatusMessage("Exception: " + ex.ToString(), StatusSeverity.Error);
                return null;
            }
        }

        private async Task<String> ProcessCurrentVideoFrameForQRCodeAsync()
        {
            // If a lock is being held it means we're still waiting for processing work on the previous frame to complete.
            // In this situation, don't wait on the semaphore but exit immediately.
            if (!frameProcessingSemaphore.Wait(0))
            {
                return null;
            }

            var br = new BarcodeReader();

            try
            {
                const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Nv12;
                using (var previewFrame = new VideoFrame(InputPixelFormat, (int)videoProperties.Width, (int)videoProperties.Height))
                {
                    await mediaCapture.GetPreviewFrameAsync(previewFrame);
                    var decoded = br.Decode(previewFrame.SoftwareBitmap);
                    return (decoded != null) ?
                        decoded.Text :
                        null;
                }
            }

            finally
            {
                frameProcessingSemaphore.Release();
            }
        }


        /// <summary>
        /// Extracts a frame from the camera stream and detects if any faces are found. Used as a precursor to making an expensive API
        /// call to get proper face details.
        /// </summary>
        /// <remarks>
        /// Keep in mind this method is called from a Timer and not synchronized with the camera stream. Also, the processing time of FaceTracker
        /// will vary depending on the size of each frame and the number of faces being tracked. That is, a large image with several tracked faces may
        /// take longer to process.
        /// </remarks>
        private async Task<ApiRequestParameters> ProcessCurrentVideoFrameAsync()
        {
            // If a lock is being held it means we're still waiting for processing work on the previous frame to complete.
            // In this situation, don't wait on the semaphore but exit immediately.
            if (!frameProcessingSemaphore.Wait(0))
            {
                return null;
            }

            try
            {
                // Create a VideoFrame object specifying the pixel format we want our capture image to be (NV12 bitmap in this case).
                // GetPreviewFrame will convert the native webcam frame into this format.
                const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Nv12;
                using (var previewFrame = new VideoFrame(InputPixelFormat, (int)videoProperties.Width, (int)videoProperties.Height))
                {
                    await mediaCapture.GetPreviewFrameAsync(previewFrame);

                    // The returned VideoFrame should be in the supported NV12 format but we need to verify this.
                    if (!FaceDetector.IsBitmapPixelFormatSupported(previewFrame.SoftwareBitmap.BitmapPixelFormat))
                    {
                        throw new NotSupportedException("PixelFormat '" + InputPixelFormat.ToString() + "' is not supported by FaceDetector");
                    }


                    var faces = await faceTracker.ProcessNextFrameAsync(previewFrame);
                    if (faces.Any())
                    {
                        // Found faces so create a bounding rectangle and store the parameters to make the API call and process the response.
                        using (var ms = new MemoryStream())
                        {
                            // It'll be faster to send a smaller rectangle of the faces found instead of the whole image. This is what we do here.
                            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms.AsRandomAccessStream());

                            // To use the encoder to resize we need to change the bitmap format. Might be a better way to do this, I can't see it.
                            var converted = SoftwareBitmap.Convert(previewFrame.SoftwareBitmap, BitmapPixelFormat.Rgba16);

                            encoder.SetSoftwareBitmap(converted);
                            //var bounds = boundingBoxCreator.BoundingBoxForFaces(faces, converted.PixelWidth, converted.PixelHeight);
                            //encoder.BitmapTransform.Bounds = bounds;
                            await encoder.FlushAsync();

                            LogStatusMessage($"Found face(s) on camera: {faces.Count}", StatusSeverity.Info);


                            return new ApiRequestParameters
                            {
                                Image = ms.ToArray(),
                                Faces = faces,
                                //ImageBounds = bounds,
                                //OriginalImageHeight = converted.PixelHeight,
                                //OriginalImageWidth = converted.PixelWidth,
                            };
                        }
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                LogStatusMessage("Unable to process current frame: " + ex.ToString(), StatusSeverity.Error);
                return null;
            }
            finally
            {
                frameProcessingSemaphore.Release();
            }
        }

        private async Task<bool> AreFacesStillPresent()
        {

            try
            {
                const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Nv12;
                using (var previewFrame = new VideoFrame(InputPixelFormat, (int)videoProperties.Width, (int)videoProperties.Height))
                {
                    await mediaCapture.GetPreviewFrameAsync(previewFrame);

                    var faces = await faceTracker.ProcessNextFrameAsync(previewFrame);
                    return faces.Any();
                }
            }
            catch (Exception ex)
            {
                LogStatusMessage("Unable to process current frame: " + ex.ToString(), StatusSeverity.Error);
                return false;  //TODO ? true or false?
            }
            finally
            {
                frameProcessingSemaphore.Release();
            }
        }


        /// <summary>
        /// Manages the scenario's internal state. Invokes the internal methods and updates the UI according to the
        /// passed in state value. Handles failures and resets the state if necessary.
        /// </summary>
        /// <param name="newState">State to switch to</param>
        private async void ChangeDetectionState(DetectionStates newState)
        {
            switch (newState)
            {
                case DetectionStates.Idle:
                    ShutdownWebCam();
                    VisualizationCanvas.Children.Clear();
                    CurrentState.State = newState;
                    break;
                case DetectionStates.Startup:
                    if (!await StartWebcamStreaming())
                    {
                        ChangeDetectionState(DetectionStates.Idle);
                        break;
                    }
                    VisualizationCanvas.Children.Clear();
                    //this needs to test for ifGUID as stored
                    var deviceId = ApplicationData.Current.LocalSettings.Values[_DeviceIdKey];
                    if (deviceId == null)
                    {
                        Say("I have no device ID. I'm now onboarding which means I am looking for a QR code containing a device ID GUID, you can get this from the device API.");
                        ChangeDetectionState(DetectionStates.OnBoarding);
                    }
                    else
                    {
                        ChangeDetectionState(DetectionStates.WaitingForFaces);
                    }
                    break;
                default:
                    CurrentState.State = newState;
                    break;
            }
        }

        /// <summary>
        /// Handles MediaCapture stream failures by shutting down streaming and returning to Idle state.
        /// </summary>
        /// <param name="sender">The source of the event, i.e. our MediaCapture object</param>
        /// <param name="args">Event data</param>
        private void MediaCapture_CameraStreamFailed(MediaCapture sender, object args)
        {
            // MediaCapture is not Agile and so we cannot invoke its methods on this caller's thread
            // and instead need to schedule the state change on the UI thread.
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                ChangeDetectionState(DetectionStates.Idle);
            });
        }
    }
}
