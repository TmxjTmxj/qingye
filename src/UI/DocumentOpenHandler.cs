using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Diagnostics;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit;
using Qingye.Core;

namespace Qingye.UI
{
    internal sealed class DocumentOpenHandler
    {
        readonly MainWindow owner;
        internal DocumentOpenHandler(MainWindow window)
        {
            owner=window;
        }
        internal void NewBlank(bool append)
        {
            if(!append && !owner.CloseDocuments(owner.Session.Documents.ToArray()))return;
            if(!append)owner.ClearWorkspace();
            var doc=new Document();owner.Session.Open(doc,true);owner.primary.Bind(doc);owner.active=owner.primary;
            if(!owner.showSecondary)owner.SetLayout("single");
            if(owner.showSecondary)owner.secondary.Bind(owner.Session.Documents.FirstOrDefault(d=>d!=doc)??doc);
            owner.RefreshTabs();owner.UpdateStatus();
            if(owner.showPreview)owner.Observe(owner.UpdatePreviewAsync());
        }
        internal async Task ExecuteAsync(string[] paths,bool append)
        {
            if(owner.closing)return;
            owner.busy=true;
            try
            {
                string[] normalized=paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                string workspace=normalized.FirstOrDefault(Directory.Exists);
                string[] files=normalized.Where(p=>!Directory.Exists(p)).ToArray();
                if(files.Length==0)
                {
                    if(workspace!=null)
                    {
                        owner.ImportWorkspace(workspace);
                        await owner.WorkspaceLoadTask;
                    }
                    return;
                }
                string[] selected=append?files:files.Take(1).ToArray();
                var loaded=new List<Document>();
                foreach(string path in selected)
                {
                    var existing=owner.Session.Documents.FirstOrDefault(d=>d.Path!=null && String.Equals(d.Path,Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase));
                    if(append && existing!=null)
                    {
                        loaded.Add(existing);
                        continue;
                    }
                    var doc=await Task.Run(delegate
                    {
                        return FileService.Read(path);
                    });
                    loaded.Add(doc);
                }
                if(owner.closing)return;
                if(!append && !owner.CloseDocuments(owner.Session.Documents.ToArray()))return;
                if(!append)owner.ClearWorkspace();
                else
                {
                    var empty=owner.Session.Documents.Where(d=>d.Path==null && !d.IsDirty && owner.buffers.ContainsKey(d) && owner.buffers[d].TextLength==0).ToArray();
                    foreach(var doc in empty)
                    {
                        owner.Session.Documents.Remove(doc);
                        owner.buffers.Remove(doc);
                        owner.copiedVersions.Remove(doc);
                    }
                }
                foreach(var doc in loaded)if(!owner.Session.Documents.Contains(doc))owner.Session.Open(doc,true);
                if(workspace!=null)
                {
                    owner.ImportWorkspace(workspace);
                    await owner.WorkspaceLoadTask;
                }
                if(loaded.Count>0)
                {
                    owner.primary.Bind(loaded[loaded.Count-1]);
                    owner.active=owner.primary;
                    if(owner.showSecondary)
                    {
                        var other=owner.Session.Documents.FirstOrDefault(d=>d!=owner.primary.BoundDocument);
                        owner.secondary.Bind(other??owner.primary.BoundDocument);
                    }
                    owner.primary.Apply(owner.Appearance);
                    owner.secondary.Apply(owner.Appearance);
                }
                if(!append)owner.SetLayout("single");
                owner.RefreshTabs();
                owner.UpdateStatus();
                owner.primary.Editor.Focus();
                await owner.OpenSpecialViewerAsync();
                if(owner.showPreview)await owner.UpdatePreviewAsync();
            }
            catch(Exception ex)
            {
                owner.SetNotice("无法打开："+ex.Message);
            }
            finally
            {
                owner.busy=false;
                owner.openQueue.Resume();
            }
        }
    }
}
