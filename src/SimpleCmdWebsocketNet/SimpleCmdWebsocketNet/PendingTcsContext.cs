using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SimpleCmdWebsocketNet
{
    public class PendingTcsContext
    {
        public TaskCompletionSource<object> Tcs { get; set; }
        public DateTime CreateTime { get; set; }
    }
}
