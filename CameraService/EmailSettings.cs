using System;
using System.Collections.Generic;
using System.Text;

namespace CameraService
{
    // https://www.infoworld.com/article/3534690/how-to-send-emails-in-aspnet-core.html
    public class EmailSettings
    {

        public string Sender { get; set; }
        public string Reciever { get; set; }
        public string SmtpServer { get; set; }
        public int Port { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }        

    }
}
