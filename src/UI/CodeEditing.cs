using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Indentation;
namespace Qingye.UI
{
    // Editing conveniences that make source files feel like a normal code
    // editor: block-aware indentation, Tab/Shift+Tab, bracket pairing, line
    // comment toggling, duplicate line and move line. Everything lives on the
    // TextEditor itself, so lists, dialogs and the Markdown rich editor keep
    // their own keyboard behavior. Attach installs handlers once per editor,
    // Configure only swaps the language profile when a document is bound.
    internal static class CodeEditing
    {
        const int IndentSize=4;
        const string IndentUnit="    ";
        const string OpenChars="([{\"'";
        const string CloseChars=")]}\"'";
        static readonly ConditionalWeakTable<TextEditor,State> states=new ConditionalWeakTable<TextEditor,State>();
        static readonly string[] PythonExtensions={".py",".pyw"};
        static readonly string[] ProseExtensions={".md",".markdown",".mdown",".mkd",".txt",".text",".log",".csv",".tsv"};
        static readonly string[] BatchExtensions={".bat",".cmd",".mk"};
        static readonly string[] CodeExtensions={
            ".cs",".js",".mjs",".cjs",".jsx",".ts",".tsx",".java",".kt",".kts",".go",".rs",
            ".c",".h",".cpp",".hpp",".cc",".cxx",".php",".css",".scss",".less",".sql",".lua",
            ".hs",".py",".pyw",".rb",".sh",".bash",".ps1",".bat",".cmd",".json",".jsonc",
            ".xml",".xaml",".config",".csproj",".svg",".html",".htm",".yaml",".yml",".ini",
            ".toml",".cfg",".conf",".env",".properties",".r",".m",".swift",".vb",".vue",
            ".svelte",".mk"};
        static readonly string[] HashComments={
            ".py",".pyw",".ps1",".sh",".bash",".rb",".yaml",".yml",".ini",".toml",".cfg",
            ".conf",".env",".properties",".mk"};
        static readonly string[] SlashComments={
            ".cs",".js",".mjs",".cjs",".jsx",".ts",".tsx",".java",".kt",".kts",".go",".rs",
            ".c",".h",".cpp",".hpp",".cc",".cxx",".php",".css",".scss",".less",".vue",".svelte"};
        static readonly string[] DashComments={".sql",".lua",".hs"};
        static readonly string[] RemComments={".bat",".cmd"};
        static readonly string[] BlockComments={".html",".htm",".xml",".svg",".xaml",".config",".csproj"};
        static readonly string[] DedentWords={"else","elif","except","finally","case"};

        sealed class State
        {
            internal string Extension;
            internal string CommentPrefix;
            internal bool IsCode,Pairing,Python,BlockComment,Busy;
            internal State(){Extension="";}
        }

        sealed class CodeIndentationStrategy : IIndentationStrategy
        {
            internal static readonly CodeIndentationStrategy Instance=new CodeIndentationStrategy();
            public void IndentLine(TextDocument document,DocumentLine line)
            {
                if(document==null || line==null)return;
                DocumentLine previous=line.PreviousLine;
                if(previous==null)return;
                string indent=LeadingWhitespace(document,previous);
                string text=document.GetText(previous.Offset,previous.Length).TrimEnd();
                if(text.Length>0)
                {
                    char last=text[text.Length-1];
                    if(last=='{' || last=='(' || last=='[')indent+=IndentUnit;
                }
                SetIndent(document,line,indent);
            }
            public void IndentLines(TextDocument document,int beginLine,int endLine)
            {
                if(document==null)return;
                for(int number=beginLine;number<=endLine;number++)IndentLine(document,document.GetLineByNumber(number));
            }
        }

        sealed class PythonIndentationStrategy : IIndentationStrategy
        {
            internal static readonly PythonIndentationStrategy Instance=new PythonIndentationStrategy();
            public void IndentLine(TextDocument document,DocumentLine line)
            {
                if(document==null || line==null)return;
                DocumentLine previous=line.PreviousLine;
                if(previous==null)return;
                string previousText=document.GetText(previous.Offset,previous.Length).TrimEnd();
                string indent=LeadingWhitespace(document,previous);
                string current=document.GetText(line.Offset,line.Length).TrimStart();
                string word=FirstWord(current);
                if(word!=null && IsOneOf(DedentWords,word))indent=TrimIndent(indent);
                else if(previousText.Length>0 && previousText[previousText.Length-1]==':')indent+=IndentUnit;
                SetIndent(document,line,indent);
            }
            public void IndentLines(TextDocument document,int beginLine,int endLine)
            {
                if(document==null)return;
                for(int number=beginLine;number<=endLine;number++)IndentLine(document,document.GetLineByNumber(number));
            }
        }

        // Batches the text edits of one command so the whole operation is a
        // single undo step and the cursor/selection can be mapped across it.
        sealed class Edits
        {
            readonly List<int> offsets=new List<int>();
            readonly List<int> lengths=new List<int>();
            readonly List<string> texts=new List<string>();
            internal int Count { get { return offsets.Count; } }
            internal void Add(int offset,int length,string text)
            {
                if(text==null)text="";
                int index=0;
                while(index<offsets.Count && offsets[index]<offset)index++;
                offsets.Insert(index,offset);lengths.Insert(index,length);texts.Insert(index,text);
            }
            internal void Apply(TextDocument document)
            {
                for(int index=offsets.Count-1;index>=0;index--)document.Replace(offsets[index],lengths[index],texts[index]);
            }
            internal int Map(int offset)
            {
                int shift=0;
                for(int index=0;index<offsets.Count;index++)
                {
                    int start=offsets[index],oldLength=lengths[index],newLength=texts[index].Length;
                    if(offset>=start+oldLength)shift+=newLength-oldLength;
                    else if(offset>=start)
                    {
                        int inside=offset-start;
                        return start+shift+(inside<newLength?inside:newLength);
                    }
                    else break;
                }
                return offset+shift;
            }
        }

        internal static void Attach(TextEditor editor)
        {
            if(editor==null)return;
            State existing;
            if(states.TryGetValue(editor,out existing))return;
            State state=new State();
            states.Add(editor,state);
            editor.TextArea.PreviewKeyDown+=delegate(object sender,KeyEventArgs e){HandleKeyDown(editor,state,e);};
            editor.TextArea.TextEntering+=delegate(object sender,TextCompositionEventArgs e){HandleTextEntering(editor,state,e);};
        }

        internal static void Configure(TextEditor editor,string extension)
        {
            if(editor==null)return;
            Attach(editor);
            State state;
            if(!states.TryGetValue(editor,out state))return;
            extension=Normalize(extension);
            bool python=IsOneOf(PythonExtensions,extension);
            bool code=python || IsOneOf(CodeExtensions,extension);
            state.Extension=extension;
            state.Python=python;
            state.IsCode=code;
            state.Pairing=code;
            state.CommentPrefix=IsOneOf(HashComments,extension)?"# " :
                IsOneOf(SlashComments,extension)?"// " :
                IsOneOf(DashComments,extension)?"-- " :
                IsOneOf(RemComments,extension)?"rem " : null;
            state.BlockComment=IsOneOf(BlockComments,extension);
            TextEditorOptions options=editor.Options;
            options.IndentationSize=IndentSize;
            if(code)options.ConvertTabsToSpaces=!IsOneOf(BatchExtensions,extension);
            else options.ConvertTabsToSpaces=false;
            editor.TextArea.IndentationStrategy=python?PythonIndentationStrategy.Instance:
                code?CodeIndentationStrategy.Instance:(IIndentationStrategy)new DefaultIndentationStrategy();
        }

        internal static void ToggleComment(TextEditor editor)
        {
            if(editor==null || editor.IsReadOnly || editor.Document==null)return;
            State state=StateOf(editor);
            if(state==null)return;
            if(state.BlockComment){ToggleBlockComment(editor);return;}
            if(state.CommentPrefix==null || state.CommentPrefix.Length==0)return;
            TextDocument document=editor.Document;
            string marker=state.CommentPrefix;
            string detect=marker.TrimEnd();
            int firstLine,lastLine;
            bool selected;
            SelectedLines(editor,out firstLine,out lastLine,out selected);
            bool allCommented=true,hasContent=false;
            for(int number=firstLine;number<=lastLine;number++)
            {
                DocumentLine line=document.GetLineByNumber(number);
                string text=document.GetText(line.Offset,line.Length);
                if(text.Trim().Length==0)continue;
                hasContent=true;
                if(!IsCommented(text,detect)){allCommented=false;break;}
            }
            if(!hasContent)allCommented=false;
            Edits edits=new Edits();
            for(int number=firstLine;number<=lastLine;number++)
            {
                DocumentLine line=document.GetLineByNumber(number);
                int indent=LeadingWhitespaceLength(document,line);
                if(allCommented)
                {
                    string text=document.GetText(line.Offset,line.Length);
                    int remove=CommentLength(text,indent,detect,marker);
                    if(remove>0)edits.Add(line.Offset+indent,remove,"");
                }
                else
                {
                    string text=document.GetText(line.Offset,line.Length);
                    if(!IsCommented(text,detect))edits.Add(line.Offset+indent,0,marker);
                }
            }
            ApplyEdits(editor,edits);
        }

        internal static void DuplicateLine(TextEditor editor)
        {
            if(editor==null || editor.IsReadOnly || editor.Document==null)return;
            TextDocument document=editor.Document;
            int selectionStart=editor.SelectionStart,selectionLength=editor.SelectionLength;
            string duplicate;
            DocumentLine line;
            if(selectionLength>0)
            {
                duplicate=document.GetText(selectionStart,selectionLength);
                line=document.GetLineByOffset(ClampOffset(document,selectionStart+selectionLength));
                if(selectionStart+selectionLength==line.Offset && line.PreviousLine!=null)line=line.PreviousLine;
            }
            else
            {
                line=document.GetLineByOffset(ClampOffset(document,editor.CaretOffset));
                duplicate=document.GetText(line.Offset,line.Length);
            }
            string newline=NewLineOf(document,line.LineNumber);
            int insertionOffset=line.EndOffset;
            document.UndoStack.StartUndoGroup();
            try{document.Insert(insertionOffset,newline+duplicate);}
            finally{document.UndoStack.EndUndoGroup();}
            int copyStart=insertionOffset+newline.Length;
            if(duplicate.Length==0)editor.CaretOffset=copyStart;
            else editor.Select(copyStart,duplicate.Length);
        }

        internal static void MoveLines(TextEditor editor,int direction)
        {
            if(editor==null || editor.IsReadOnly || editor.Document==null)return;
            if(direction!=-1 && direction!=1)return;
            TextDocument document=editor.Document;
            int selectionStart=editor.SelectionStart,selectionLength=editor.SelectionLength;
            int caretOffset=editor.CaretOffset;
            int firstLine,lastLine;
            bool selected;
            SelectedLines(editor,out firstLine,out lastLine,out selected);
            int delta;
            if(direction<0)
            {
                if(firstLine<=1)return;
                DocumentLine previous=document.GetLineByNumber(firstLine-1);
                DocumentLine first=document.GetLineByNumber(firstLine);
                DocumentLine last=document.GetLineByNumber(lastLine);
                string previousText=document.GetText(previous.Offset,previous.Length);
                string blockText=document.GetText(first.Offset,last.EndOffset-first.Offset);
                string newline=NewLineOf(document,previous.LineNumber);
                int rangeStart=previous.Offset,rangeEnd=last.EndOffset;
                delta=rangeStart-first.Offset;
                document.UndoStack.StartUndoGroup();
                try{document.Replace(rangeStart,rangeEnd-rangeStart,blockText+newline+previousText);}
                finally{document.UndoStack.EndUndoGroup();}
            }
            else
            {
                DocumentLine first=document.GetLineByNumber(firstLine);
                DocumentLine last=document.GetLineByNumber(lastLine);
                DocumentLine next=last.NextLine;
                if(next==null)return;
                string nextText=document.GetText(next.Offset,next.Length);
                string blockText=document.GetText(first.Offset,last.EndOffset-first.Offset);
                string newline=NewLineOf(document,last.LineNumber);
                int rangeStart=first.Offset,rangeEnd=next.EndOffset;
                delta=rangeStart+nextText.Length+newline.Length-first.Offset;
                document.UndoStack.StartUndoGroup();
                try{document.Replace(rangeStart,rangeEnd-rangeStart,nextText+newline+blockText);}
                finally{document.UndoStack.EndUndoGroup();}
            }
            if(selected)
            {
                int start=selectionStart+delta;
                if(start<0)start=0;
                if(start>document.TextLength)start=document.TextLength;
                int length=selectionLength;
                if(start+length>document.TextLength)length=document.TextLength-start;
                editor.Select(start,length);
            }
            else
            {
                int caret=caretOffset+delta;
                if(caret<0)caret=0;
                if(caret>document.TextLength)caret=document.TextLength;
                editor.CaretOffset=caret;
            }
        }

        internal static void IndentLines(TextEditor editor,bool unindent)
        {
            if(editor==null || editor.IsReadOnly || editor.Document==null)return;
            TextDocument document=editor.Document;
            int firstLine,lastLine;
            bool selected;
            SelectedLines(editor,out firstLine,out lastLine,out selected);
            Edits edits=new Edits();
            for(int number=firstLine;number<=lastLine;number++)
            {
                DocumentLine line=document.GetLineByNumber(number);
                if(unindent)
                {
                    int remove=0;
                    if(line.Offset<line.EndOffset && document.GetCharAt(line.Offset)=='\t')remove=1;
                    else
                        while(remove<IndentSize && line.Offset+remove<line.EndOffset && document.GetCharAt(line.Offset+remove)==' ')remove++;
                    if(remove>0)edits.Add(line.Offset,remove,"");
                }
                else edits.Add(line.Offset,0,IndentUnit);
            }
            ApplyEdits(editor,edits);
        }

        internal static bool CompletePair(TextEditor editor,string text)
        {
            if(editor==null || editor.IsReadOnly || editor.Document==null)return false;
            if(text==null || text.Length!=1)return false;
            State state=StateOf(editor);
            if(state==null || !state.Pairing || state.Busy)return false;
            char typed=text[0];
            TextDocument document=editor.Document;
            int openIndex=OpenChars.IndexOf(typed);
            if(openIndex>=0)
            {
                char close=CloseChars[openIndex];
                int start=editor.SelectionStart,length=editor.SelectionLength;
                state.Busy=true;
                try
                {
                    document.UndoStack.StartUndoGroup();
                    try
                    {
                        if(length>0)
                        {
                            string selected=document.GetText(start,length);
                            document.Replace(start,length,typed+selected+close);
                            editor.Select(start+1,selected.Length);
                        }
                        else
                        {
                            if(typed==close && start<document.TextLength && document.GetCharAt(start)==typed)
                            {
                                editor.CaretOffset=start+1;
                                return true;
                            }
                            document.Insert(start,typed+close.ToString());
                            editor.CaretOffset=start+1;
                        }
                    }
                    finally{document.UndoStack.EndUndoGroup();}
                }
                finally{state.Busy=false;}
                return true;
            }
            int closeIndex=CloseChars.IndexOf(typed);
            if(closeIndex>=0 && editor.SelectionLength==0)
            {
                int offset=editor.CaretOffset;
                if(offset<document.TextLength && document.GetCharAt(offset)==typed)
                {
                    state.Busy=true;
                    try{editor.CaretOffset=offset+1;}
                    finally{state.Busy=false;}
                    return true;
                }
            }
            return false;
        }

        static void ToggleBlockComment(TextEditor editor)
        {
            TextDocument document=editor.Document;
            const string open="<!--";
            const string close="-->";
            int start=editor.SelectionStart,length=editor.SelectionLength;
            Edits edits=new Edits();
            bool hadSelection=length>0;
            int selectStart,selectLength;
            if(hadSelection)
            {
                string text=document.GetText(start,length);
                bool selfWrapped=text.Length>=open.Length+close.Length && text.StartsWith(open,StringComparison.Ordinal) && text.EndsWith(close,StringComparison.Ordinal);
                if(selfWrapped)
                {
                    selectStart=start;
                    selectLength=text.Length-open.Length-close.Length;
                    edits.Add(start,length,text.Substring(open.Length,selectLength));
                }
                else if(start>=open.Length && start+length+close.Length<=document.TextLength &&
                    document.GetText(start-open.Length,open.Length)==open &&
                    document.GetText(start+length,close.Length)==close)
                {
                    selectStart=start-open.Length;
                    selectLength=length;
                    edits.Add(selectStart,length+open.Length+close.Length,text);
                }
                else
                {
                    selectStart=start+open.Length;
                    selectLength=length;
                    edits.Add(start,length,open+text+close);
                }
            }
            else
            {
                DocumentLine line=document.GetLineByOffset(ClampOffset(document,editor.CaretOffset));
                int indent=LeadingWhitespaceLength(document,line);
                int contentLength=line.Length-indent;
                string content=document.GetText(line.Offset+indent,contentLength);
                bool wrappedLine=content.Length>=open.Length+close.Length && content.StartsWith(open,StringComparison.Ordinal) && content.EndsWith(close,StringComparison.Ordinal);
                if(wrappedLine)
                {
                    selectLength=content.Length-open.Length-close.Length;
                    edits.Add(line.Offset+indent,contentLength,content.Substring(open.Length,selectLength));
                }
                else
                {
                    selectLength=0;
                    edits.Add(line.Offset+indent,contentLength,open+content+close);
                }
                selectStart=line.Offset+indent+(wrappedLine?0:open.Length);
            }
            ApplyEdits(editor,edits);
            if(hadSelection)editor.Select(selectStart,selectLength);
            else editor.CaretOffset=selectStart;
        }

        static void HandleKeyDown(TextEditor editor,State state,KeyEventArgs e)
        {
            if(e.Handled || editor.IsReadOnly || editor.Document==null)return;
            ModifierKeys modifiers=Keyboard.Modifiers;
            bool control=(modifiers & ModifierKeys.Control)!=0;
            bool shift=(modifiers & ModifierKeys.Shift)!=0;
            bool alt=(modifiers & ModifierKeys.Alt)!=0;
            Key key=e.Key;
            if(control && !shift && !alt && key==Key.D){DuplicateLine(editor);e.Handled=true;return;}
            if(alt && !control && (key==Key.Up || key==Key.Down)){MoveLines(editor,key==Key.Up?-1:1);e.Handled=true;return;}
            if(control && !shift && !alt && (key==Key.OemQuestion || key==Key.Divide || key==Key.Oem2)){ToggleComment(editor);e.Handled=true;return;}
            if(key==Key.Tab && !control && !alt)
            {
                if(shift){IndentLines(editor,true);e.Handled=true;return;}
                if(editor.SelectionLength>0 || SelectionSpansLines(editor)){IndentLines(editor,false);e.Handled=true;}
            }
        }

        static void HandleTextEntering(TextEditor editor,State state,TextCompositionEventArgs e)
        {
            if(e.Handled || state.Busy || !state.Pairing)return;
            if(e.Text==null || e.Text.Length!=1)return;
            if(CompletePair(editor,e.Text))e.Handled=true;
        }

        static State StateOf(TextEditor editor)
        {
            State state;
            return states.TryGetValue(editor,out state)?state:null;
        }

        static void SelectedLines(TextEditor editor,out int firstLine,out int lastLine,out bool selected)
        {
            TextDocument document=editor.Document;
            int start=ClampOffset(document,editor.SelectionStart);
            int length=editor.SelectionLength;
            selected=length>0;
            if(!selected)
            {
                int number=document.GetLineByOffset(start).LineNumber;
                firstLine=number;lastLine=number;return;
            }
            DocumentLine first=document.GetLineByOffset(start);
            int end=ClampOffset(document,start+length);
            DocumentLine last=document.GetLineByOffset(end);
            if(end==last.Offset && end>first.Offset && last.PreviousLine!=null)last=last.PreviousLine;
            firstLine=first.LineNumber;lastLine=last.LineNumber;
        }

        static bool SelectionSpansLines(TextEditor editor)
        {
            if(editor.SelectionLength<=0 || editor.Document==null)return false;
            TextDocument document=editor.Document;
            int start=ClampOffset(document,editor.SelectionStart);
            int end=ClampOffset(document,start+editor.SelectionLength);
            return document.GetLineByOffset(start).LineNumber!=document.GetLineByOffset(end).LineNumber;
        }

        static int ClampOffset(TextDocument document,int offset)
        {
            if(offset<0)return 0;
            if(offset>document.TextLength)return document.TextLength;
            return offset;
        }

        static string Normalize(string extension)
        {
            if(extension==null)return "";
            string value=extension.Trim().ToLowerInvariant();
            if(value.Length>0 && value[0]!='.')value="."+value;
            return value;
        }

        static bool IsOneOf(string[] values,string value)
        {
            if(value==null)return false;
            for(int index=0;index<values.Length;index++)if(values[index]==value)return true;
            return false;
        }

        static string LeadingWhitespace(TextDocument document,DocumentLine line)
        {
            return document.GetText(line.Offset,LeadingWhitespaceLength(document,line));
        }

        static int LeadingWhitespaceLength(TextDocument document,DocumentLine line)
        {
            int length=0;
            while(line.Offset+length<line.EndOffset)
            {
                char c=document.GetCharAt(line.Offset+length);
                if(c!=' ' && c!='\t')break;
                length++;
            }
            return length;
        }

        static void SetIndent(TextDocument document,DocumentLine line,string indent)
        {
            int length=LeadingWhitespaceLength(document,line);
            if(document.GetText(line.Offset,length)==indent)return;
            document.Replace(line.Offset,length,indent);
        }

        static string TrimIndent(string indent)
        {
            if(indent.Length==0)return indent;
            if(indent[indent.Length-1]=='\t')return indent.Substring(0,indent.Length-1);
            int count=0;
            while(count<IndentSize && indent.Length-count>0 && indent[indent.Length-1-count]==' ')count++;
            return indent.Substring(0,indent.Length-count);
        }

        static string FirstWord(string text)
        {
            if(text.Length==0)return null;
            char first=text[0];
            if(!((first>='a' && first<='z') || (first>='A' && first<='Z') || first=='_'))return null;
            int length=0;
            while(length<text.Length)
            {
                char c=text[length];
                if((c>='a' && c<='z') || (c>='A' && c<='Z') || (c>='0' && c<='9') || c=='_')length++;
                else break;
            }
            return text.Substring(0,length);
        }

        static bool IsCommented(string text,string detect)
        {
            string trimmed=text.TrimStart();
            if(detect.Length==0 || trimmed.Length<detect.Length)return false;
            if(!trimmed.StartsWith(detect,StringComparison.Ordinal))return false;
            if(char.IsLetter(detect[0]))
            {
                if(trimmed.Length==detect.Length)return true;
                char next=trimmed[detect.Length];
                return next==' ' || next=='\t';
            }
            return true;
        }

        static int CommentLength(string text,int indent,string detect,string marker)
        {
            if(indent>text.Length)return 0;
            string rest=text.Substring(indent);
            if(rest.StartsWith(marker,StringComparison.Ordinal))return marker.Length;
            if(rest.StartsWith(detect,StringComparison.Ordinal))
            {
                int length=detect.Length;
                if(rest.Length>length && (rest[length]==' ' || rest[length]=='\t'))length++;
                return length;
            }
            return 0;
        }

        static string NewLineOf(TextDocument document,int lineNumber)
        {
            DocumentLine line=document.GetLineByNumber(lineNumber);
            if(line.DelimiterLength>0)return document.GetText(line.EndOffset,line.DelimiterLength);
            DocumentLine previous=line.PreviousLine;
            if(previous!=null && previous.DelimiterLength>0)return document.GetText(previous.EndOffset,previous.DelimiterLength);
            return Environment.NewLine;
        }

        static void ApplyEdits(TextEditor editor,Edits edits)
        {
            if(edits.Count==0)return;
            TextDocument document=editor.Document;
            int start=editor.SelectionStart,length=editor.SelectionLength;
            document.UndoStack.StartUndoGroup();
            try{edits.Apply(document);}
            finally{document.UndoStack.EndUndoGroup();}
            int mappedStart=edits.Map(start);
            if(mappedStart<0)mappedStart=0;
            if(mappedStart>document.TextLength)mappedStart=document.TextLength;
            int mappedLength=0;
            if(length>0)
            {
                mappedLength=edits.Map(start+length)-mappedStart;
                if(mappedLength<0)mappedLength=0;
                if(mappedStart+mappedLength>document.TextLength)mappedLength=document.TextLength-mappedStart;
            }
            if(length>0)editor.Select(mappedStart,mappedLength);
            else editor.CaretOffset=mappedStart;
        }
    }
}
