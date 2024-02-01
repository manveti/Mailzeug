using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Mailzeug {
    public class MZError : Exception {
        public MZError() : base() { }
        public MZError(string message) : base(message) { }
        public MZError(string message, Exception innerException) : base(message, innerException) { }
    }
}
