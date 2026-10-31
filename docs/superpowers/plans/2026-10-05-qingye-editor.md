# Qingye Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a small Windows executable for fluent text reading/simple editing, optional memory-only workspace import and adjustable bright layouts.

**Architecture:** WPF native UI and AvalonEdit on system .NET Framework 4.8. Pure file/session code owns encoding, atomic saves and close decisions. Preview modules load WebView2 only on demand and use an isolated temporary profile disposed on close.

**Tech Stack:** C# 5, WPF, AvalonEdit net462, WebView2 net462, Markdig if a compatible target is available, PowerShell build scripts; system framework compiler with no bundled SDK.

**Spec:** `docs/superpowers/specs/2026-10-05-qingye-editor-design.md`

## Global Constraints

- Default open replaces current session with one file; manual import adds files or a folder.
- No welcome/module/project wizard, file MRU, workspace persistence, autosave or session restore.
- Save UI preferences only after explicit opt-in; settings contain no paths or content.
- Text >20 MB disables highlighting; >128 MB opens read-only in segments <=4 MB.
- XML preview entry <=16 MB; total <=64 MB; sheet preview <=500 rows/50 columns.
- Native text mode never initializes WebView2; close preview disposes all owned resources.
- Application release directory target <=15 MB; performance reported from measurements.

## Review Focus

1. Cancelled replace/close must retain all dirty documents.
2. External modification/deletion and failed save must not silently destroy content.
3. Chinese encoding/BOM/mixed newlines and Unicode boundaries must survive round trips.
4. Binary/archive/huge text inputs must have bounded reads and never editable binary output.
5. Hidden/reopened previews and repeated open/close must not retain sessions or processes.

## File structure

- `src/Core/Document.cs`, `FileService.cs`, `DocumentSession.cs`, `Settings.cs`, `ContentReaders.cs`: pure document data, bounded read/save/session rules and preview extraction.
- `src/UI/MainWindow.cs`, `EditorPane.cs`, `Theme.cs`, `Dialogs.cs`, `PreviewPane.cs`: native controls, layouts, user operations and lazy previews.
- `src/Program.cs`: startup, single instance forwarding, application error handling.
- `scripts/restore.ps1`, `build.ps1`, `test.ps1`, `measure.ps1`: download pinned packages inside ignored build directory, compile, test, measure, package.
- `tests/CoreTests.cs`, `UiTests.cs`: real files and native window fixtures; no production self-test branches.
- `examples/`: optional synthetic fixtures, never automatically opened or copied into app state.
- `release/`, `artifacts/`: runnable app/package, QA screenshots and measurement report.

### Task 1: File/session contracts

Interfaces: `FileService.Read(string path, int page=0) -> Document`; `Save(Document doc, string target, bool force) -> void`; `DocumentSession.Open(Document doc, bool append)`, `TryClose(IEnumerable<Document>, Func<IList<Document>, CloseChoice>) -> bool`; `Settings.Load/Save(string)`.

- [ ] Write real-file tests: UTF8/UTF16/GB18030 bytes and newlines unchanged after save, external version conflict leaves both files safe, deleted target requires explicit force/Save As, binary readonly, segment limit, replace vs append, cancelled close keeps dirty files, no settings/session paths.
- [ ] Run tests RED against a compilable missing-contract implementation; see expected assertions fail.
- [ ] Implement core with strict encoders, FileShare.ReadWrite/Delete, size-bounded reads, same-directory temporary replacement and version checks.
- [ ] Run full core suite GREEN; commit.

### Task 2: Native editor and user operations

Interfaces: `MainWindow.OpenPathsAsync(string[] paths, bool append) -> Task`; `EditorPane.Bind(Document)`; `Theme.Apply(Window, Settings)`; settings and layout actions update existing controls.

- [ ] Add STA native-window tests: initial writable blank editor, open replaces, import adds, two panes bind distinct documents, switching theme/layout keeps document edits, search replacement saves exact text, opt-in persistence only.
- [ ] Run RED for missing native window behavior.
- [ ] Implement native titlebar/toolbars/tabs, find/replace, fonts/wrap, encoding/newline menus, one consolidated save-close dialog, drag/drop and folder import. File tree loads only requested level.
- [ ] Implement draggable/resizable panel layout with one/two/preview presets, placement menu and reset.
- [ ] Run core/UI suite GREEN; commit.

### Task 3: Bounded file viewers and preview

Interfaces: `ContentReaders.ReadOffice/ReadTable` return readonly content; `PreviewPane.ShowAsync(Document, IEnumerable<Document>)`, `Dispose()`; optional static content mapping reads only within explicit root.

- [ ] Add RED tests for quoted CSV, bounded Office XML/XXE refusal, hidden path traversal, escaped Markdown, no WebView2 process in text mode, preview dispose and recreation.
- [ ] Implement native images, CSV/table and Office content readers; PDF/HTML/Markdown/SVG through lazy shared WebView2. Map unsaved text resources, add refresh/local URL/viewport controls.
- [ ] Use temporary isolated profile, no background listener or telemetry; dispose preview on hide and delete temporary profile after owned processes exit.
- [ ] Run suite GREEN and actual WebView2 preview checks; commit.

### Task 4: Packaging, verification and handoff

- [ ] Compile release x64 Windows executable, copy only runtime DLLs/licences; provide direct EXE and portable ZIP, concise usage/limitations.
- [ ] Measure process-start to window/input readiness, close completion, app and owned browser memory/CPU, release directory and ZIP sizes, 1/10 MB text open, large-directory level loading.
- [ ] Capture real native pure-edit and integrated layouts; compare against selected integrated reference and write `design-qa.md` with remaining limitations.
- [ ] Fresh whole-project code review, repair material findings with regression tests, run complete suite after changes.
- [ ] Verify no file/session persistence or own-process residue after exit. Commit verified source and provide executable/package links and measured results.

## Execution notes

The user explicitly said “开始制作”; continue inline in this chat without another plan approval round. The project contains only approved design artifacts, so use a feature branch in the existing authorized project directory rather than creating another checkout. This is a documented adaptation to the user's explicit start instruction and the empty code baseline.
