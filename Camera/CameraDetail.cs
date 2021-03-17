using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text;

namespace Camera
{
    public class CameraDetail
    {
        public string SourceURL { get; set; }        
        public string FFMpegRecordingSourceURL { get; set; }
        public string StoragePath { get; set; }
        public string FFMpegPath { get; set; }
        public string Title { get; set; }        

        public double MotionThresholdPct { get; set; }
        public double MotionMinAreaPct { get; set; }
        public double MotionBackgroundAdaptSpeedPct { get; set; }

        public int NotifyFrames { get; set; }

        public Rect2d? MotionInclude { get; set; } 

        // from x1,y1,x2,y2 syntax
        public static Rect2d? MakeMotionInclude(string str)
        {
            if (str == null)
                return null;
            var floats = str.Split(',').Select(x => float.Parse(x)).ToArray();
            return new Rect2d(floats[0], floats[1], floats[2] - floats[0], floats[3] - floats[1]);
        }
    }
}
