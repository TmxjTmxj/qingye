using System;
using System.Collections.Generic;

namespace Qingye.Core
{
    public enum DocumentKind { Text, PagedText, Binary, Image, Pdf, Docx, Xlsx, Rtf, Pptx, Epub }
    public enum CloseChoice { Cancel, Discard, Save }
    public class Document
    {
        private string text = "";
        private string baseline = "";
        private Func<int, string> textSource;
        private int sourceLength;
        public string Path { get; set; }
        public string Title { get; set; }
        public string EncodingName { get; set; }
        public bool HasBom { get; set; }
        public string NewLine { get; set; }
        public bool IsReadOnly { get; set; }
        public bool Modified { get; set; }
        public bool MetadataModified { get; set; }
        public DocumentKind Kind { get; set; }
        public string Version { get; set; }
        public long ByteLength { get; set; }
        public int Page { get; set; }
        public int PageCount { get; set; }
        public int PageByteSize { get; set; }
        public string Notice { get; set; }
        public long ContentRevision {get;private set;}
        public int TextLength { get { return textSource == null ? text.Length : sourceLength; } }
        public string Text
        {
            get { if (text == null) text = textSource(sourceLength); return text; }
            set { string next=value??"";if(text!=next || textSource!=null)ContentRevision++;textSource=null;text=next;Modified=text!=baseline; }
        }
        // The caller supplies an immutable snapshot; bounded readers need no full string.
        public void SetTextSource(int length, Func<int, string> read)
        {
            if (length < 0 || read == null) throw new ArgumentException("Invalid text snapshot.");
            sourceLength = length;
            textSource = read;
            text = null;
            ContentRevision++;
        }
        public string ReadText(int maximumCharacters)
        {
            int length = Math.Min(TextLength, Math.Max(0, maximumCharacters));
            if (text != null) return length == text.Length ? text : text.Substring(0, length);
            return textSource(length);
        }
        public bool IsDirty { get { return Modified || MetadataModified; } }
        public Document() { Title = "未命名"; EncodingName = "utf-8"; NewLine = "\r\n"; PageCount = 1; }
        public void SetLoadedText(string value) { textSource=null;text = baseline = value ?? "";ContentRevision++;Modified=false; }
        public void AcceptChanges() { baseline = Text; Modified = false; MetadataModified = false; }
    }
    public class FileConflictException : Exception { public FileConflictException(string message) : base(message) { } }
}
