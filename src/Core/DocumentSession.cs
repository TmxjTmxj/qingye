using System;
using System.Collections.Generic;
using System.Linq;
namespace Qingye.Core
{
    public class DocumentSession
    {
        public IList<Document> Documents { get; private set; }
        public DocumentSession() { Documents = new List<Document>(); }
        public void Open(Document doc, bool append)
        {
            if(!append){if(Documents.Any(d=>d.IsDirty))throw new InvalidOperationException("先处理未保存的文件。");Documents.Clear();}
            if(doc.Path!=null && Documents.Any(d=>String.Equals(d.Path,doc.Path,StringComparison.OrdinalIgnoreCase)))return;
            Documents.Add(doc);
        }
        public bool TryClose(IEnumerable<Document> docs, Func<IList<Document>, CloseChoice> decide)
        {
            var requested=docs.Where(d=>Documents.Contains(d)).Distinct().ToArray();var dirty=requested.Where(d=>d.IsDirty).ToList();
            if(dirty.Count>0)
            {
                CloseChoice choice=decide(dirty);if(choice==CloseChoice.Cancel)return false;
                if(choice==CloseChoice.Save && dirty.Any(d=>d.IsDirty))return false;
            }
            foreach(var doc in requested)Documents.Remove(doc);return true;
        }
    }
}
