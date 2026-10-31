using System;
using System.IO;

namespace Qingye.Core
{
    // A forward-only response releases its backing file/array after EOF.
    // Cached Length/Position remain available to the COM stream adapter.
    public sealed class ResponseStream : Stream
    {
        readonly object gate=new object();
        readonly Action<ResponseStream> finished;
        readonly long length;
        Stream body;
        long position;
        bool notified;
        public ResponseStream(Stream input,Action<ResponseStream> completed){body=input;length=input.Length;finished=completed;}
        public override bool CanRead {get{return true;}}
        public override bool CanSeek {get{return false;}}
        public override bool CanWrite {get{return false;}}
        public override long Length {get{return length;}}
        public override long Position {get{lock(gate)return position;}set{throw new NotSupportedException();}}
        public override int Read(byte[] buffer,int offset,int count)
        {
            bool notify=false;int read;
            lock(gate){if(body==null)return 0;read=body.Read(buffer,offset,count);position+=read;if(count>0 && (read==0 || position>=length) && !notified){notified=true;notify=true;}}
            if(notify && finished!=null)finished(this);return read;
        }
        protected override void Dispose(bool disposing){lock(gate){if(body!=null){body.Dispose();body=null;}}base.Dispose(disposing);}
        public override void Flush(){}
        public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}
        public override void SetLength(long value){throw new NotSupportedException();}
        public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
    }
}
