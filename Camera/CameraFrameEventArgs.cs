using System;
using System.Collections.Generic;
using System.Text;

namespace Camera
{
    public class CameraFrameEventArgs : EventArgs
    {
        public DateTime When { get; set; }

        public bool HasMotion { get; set; }
        public byte[] JPEG { get; set; }

        public byte[] OriginalFrameJPEG { get; set; }
        public CameraDetail Detail { get; set; }

        public bool HasBeenMotionProcessed { get; set; }

       
    }
}
