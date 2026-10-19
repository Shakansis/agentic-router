# Project file references in the composer

Typing `@` at the start of a word opens a searchable list from the active project's
trusted workspace. Search matches file names and relative paths, case-insensitively.
Choose with the mouse or Up/Down followed by Enter or Tab; Escape closes the list.
Enter while the list is open inserts a reference instead of sending the message.
Email addresses do not open the list. Shift+Enter still inserts a newline.

The composer renders a removable inline badge with the file name and the relative
path as its tooltip. The message sent to the Host contains that relative path as
Markdown inline code, preserving its position in the surrounding text. Backtick
delimiters adapt to paths containing backticks. No file contents are attached or
read by this feature. Chat and Execute retain their existing file-access policies.
Copying a badge copies its path; native undo/redo and keyboard deletion work.
References known in the current tab are restored as badges when the Host restores
a prompt from the same project. Persisted messages remain ordinary text paths.

The Host endpoint `GET /api/workspace/files?workspaceId=...&query=...` pins the
active workspace while listing, rejects a stale project ID, absolute paths and
traversal, and uses the existing trusted-path validator. It does not follow reparse
points or enumerate `.git`. All file types may be referenced. Each search returns
up to 50 matches and scans up to 20,000 entries; a partial result is explicit and
the user can refine by folder/name. A path prefix starts the search in that folder.
The browser debounces and cancels obsolete searches and ignores responses for a
different active project. Listing does not invoke a model or change project files.

The inline editor preserves the existing composer value, read-only and disabled
contracts used by queueing, history restoration and harness user-input questions.
Pasted text remains plain text; existing image attachment handlers remain active.
Browser/API coverage lives in `ComposerFilesEndToEndTests.cs`.
