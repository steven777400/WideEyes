using OpenCvSharp;
using RtspClientSharp;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Camera
{
    public class RtspMJpegStills
    {
        private CameraDetail detail;
        private Task JPEGMaker;

        public delegate void CameraFrameEventHandler(object sender, CameraFrameEventArgs e);
        public event CameraFrameEventHandler CameraFrameEvent;

        private Action<string> log;

        public RtspMJpegStills(CameraDetail detail, Action<string> log)
        {
            this.detail = detail;
            this.log = log;


        }

        public async Task Run(CancellationToken stoppingToken)
        {

            DateTime start = DateTime.Now;
            if (!String.IsNullOrEmpty(detail.SourceURL)) 
            {                

                try
                {                    
                    var cp = new ConnectionParameters(new Uri(detail.SourceURL));                    

                    using (var rtspClient = new RtspClient(cp))
                    {
                        rtspClient.FrameReceived += RtspClient_FrameReceived;

                        while (!stoppingToken.IsCancellationRequested)
                        {
                            log("Connecting...");

                            await rtspClient.ConnectAsync(stoppingToken);

                            log("Connected.");

                            await rtspClient.ReceiveAsync(stoppingToken);

                        }
                    }
                }
                catch (Exception ex)
                {
                    log(ex.ToString());
                }

                
                if (JPEGMaker != null)
                    await JPEGMaker;

                CameraFrameEvent?.Invoke(this, new CameraFrameEventArgs { HasMotion = false, JPEG = null, OriginalFrameJPEG = null, When = DateTime.Now, Detail = detail });
                
                MotionAvg?.Dispose();

                log("ran to completion");

            }
        }

        private void RtspClient_FrameReceived(object sender, RtspClientSharp.RawFrames.RawFrame e)
        {
            if (e.Type == RtspClientSharp.RawFrames.FrameType.Audio)
                return;            

            if (JPEGMaker == null || JPEGMaker.IsCompleted)
            {                
                byte[] dest = new byte[e.FrameSegment.Count];
                Array.Copy(e.FrameSegment.Array, e.FrameSegment.Offset, dest, 0, e.FrameSegment.Count);
                JPEGMaker = new Task(() => ProcessFrame(dest, DateTime.Now)); // better than e.TimeStamp in case the camera TZ or whatever is wrong
                JPEGMaker.Start();
            }
        }

        private Mat MotionAvg;
        private void ProcessFrame(byte[] jpeg, DateTime when)
        {

            Mat oframe;
            try
            {
                oframe = Cv2.ImDecode(jpeg, ImreadModes.Unchanged);
            }
            catch
            {
                log("bad image");
                CameraFrameEvent?.Invoke(this, new CameraFrameEventArgs { HasMotion = false, JPEG = null, OriginalFrameJPEG = null, When = DateTime.Now, Detail = detail });
                return;
            }
            if (oframe.Width == 0 || oframe.Height == 0)
            {
                log("bad image");
                CameraFrameEvent?.Invoke(this, new CameraFrameEventArgs { HasMotion = false, JPEG = null, OriginalFrameJPEG = null, When = DateTime.Now, Detail = detail });
                return;
            }

            // Destructively resize frame to 500x500, maintaing aspect ratio
            Size newsize;
            if (oframe.Width > oframe.Height)
                newsize = new Size(500, oframe.Height * 500 / oframe.Width);
            else
                newsize = new Size(oframe.Width * 500 / oframe.Height, 500);

            Mat frame = new Mat();
            Cv2.Resize(oframe, frame, newsize, 0, 0, InterpolationFlags.Linear);
            oframe.Dispose();

            // Begin motion detection
            // https://www.pyimagesearch.com/2015/05/25/basic-motion-detection-and-tracking-with-python-and-opencv/
            // https://www.pyimagesearch.com/2015/06/01/home-surveillance-and-motion-detection-with-the-raspberry-pi
            // https://answers.opencv.org/question/63202/how-to-convert-from-cv_16s-to-cv_8u-cv_16u-cv_32f-cvtcolor-assertion-failed/
            // Mat leaks, gotta use using https://github.com/shimat/opencvsharp/issues/559
            using (Mat pre1motionframe = new Mat())
            using (Mat pre2motionframe = new Mat())
            using (Mat motionframe = new Mat())
            using (Mat frameDelta = new Mat())
            using (Mat Thres = new Mat())
            {



                frame.ConvertTo(pre1motionframe, MatType.CV_32F, 1.0 / 255.0);
                Cv2.CvtColor(pre1motionframe, pre2motionframe, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(pre2motionframe, motionframe, new Size(21, 21), 0);

                if (MotionAvg == null)
                {
                    MotionAvg = new Mat(motionframe.Size(), MatType.CV_32F);
                    motionframe.CopyTo(MotionAvg);

                }

                Cv2.AccumulateWeighted(motionframe, MotionAvg, detail.MotionBackgroundAdaptSpeedPct, new Mat());


                // cv2.absdiff(gray, cv2.convertScaleAbs(avg)) convert scale abs?


                Cv2.Absdiff(MotionAvg, motionframe, frameDelta);



                frameDelta.ConvertTo(frameDelta, MatType.CV_8UC1, 255);
                Cv2.Threshold(frameDelta, Thres, (int)(detail.MotionThresholdPct * 255.0), 255, ThresholdTypes.Binary);
                Cv2.Dilate(Thres, Thres, new Mat(), iterations: 2);

                Mat[] contours;

                using (var oh = new Mat())
                {
                    Cv2.FindContours(Thres, out contours, oh, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                }

                bool motion = false;
                Rect motionInclude = Rect.Empty;
                if (detail.MotionInclude != null)
                {
                    motionInclude = new Rect(
                        (int)(detail.MotionInclude.Value.X * (double)newsize.Width),
                        (int)(detail.MotionInclude.Value.Y * (double)newsize.Height),
                        (int)(detail.MotionInclude.Value.Width * (double)newsize.Width),
                        (int)(detail.MotionInclude.Value.Height * (double)newsize.Height));

                }
                foreach (var cnt in contours)
                {
                    if (cnt.ContourArea() >= detail.MotionMinAreaPct * newsize.Width * newsize.Height)
                    {

                        var rect = Cv2.BoundingRect(cnt);
                        if (motionInclude == Rect.Empty || rect.IntersectsWith(motionInclude))
                        {
                            motion = true;
                            Cv2.Rectangle(frame, rect, Scalar.Green, 2);
                        }
                    }
                    cnt.Dispose();

                }
                if (motionInclude != Rect.Empty && motion)
                {
                    Cv2.Rectangle(frame, motionInclude, Scalar.Blue, 1);
                }


                // Generate a JPEG binary
                byte[] result;
                Cv2.ImEncode(".jpg", frame, out result);

                frame.Dispose();                
                CameraFrameEvent?.Invoke(this, new CameraFrameEventArgs { HasMotion = motion, JPEG = result, OriginalFrameJPEG = jpeg, When = when, Detail = detail });
            }           
           
        }

    }
}
