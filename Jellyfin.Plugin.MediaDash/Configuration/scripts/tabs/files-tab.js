// files behavior and state. Dependencies are supplied by the page controller.
(function () {
    'use strict';
    window.MediaDash.createFilesTab = function (deps) {


            // ---------- Files tab ----------
            var currentFilePath = '';
            var fileRequest = 0, pickerRequest = 0;

            var currentFileListing = null;

            var fileSort = { by: 'name', dir: 'asc' };

            var fileSelection = new Set();

            var fileFilter = '';

            var picker = { open: false, path: '', listing: null, onPick: null };


            function sortEntries(entries, by, dir) {
                var sign = dir === 'desc' ? -1 : 1;
                var copy = entries.slice();
                copy.sort(function (a, b) {
                    // Directories always sort above files, regardless of column/direction.
                    if (a.IsDirectory !== b.IsDirectory) return a.IsDirectory ? -1 : 1;
                    if (by === 'size') return sign * ((a.SizeBytes || 0) - (b.SizeBytes || 0));
                    if (by === 'date') return sign * (new Date(a.ModifiedUtc) - new Date(b.ModifiedUtc));
                    return sign * a.Name.localeCompare(b.Name, undefined, { numeric: true, sensitivity: 'base' });
                });
                return copy;
            }


            function loadFiles(path) {
                var request = ++fileRequest;
                var listEl = deps.view.querySelector('#mdFileList');
                listEl.innerHTML = '<div class="fieldDescription">Loading…</div>';
                // Selection is only meaningful within one folder; clear when navigating.
                fileSelection.clear();
                var url = 'Files/List' + (path ? '?path=' + encodeURIComponent(path) : '');
                deps.api(url).then(function (d) {
                    if (request !== fileRequest) return;
                    currentFilePath = d.Path || '';
                    renderFileList(d);
                }, function (xhr) {
                    if (request !== fileRequest) return;
                    listEl.innerHTML = '<div class="fieldDescription" style="color:#ef4444;">' + (xhr && xhr.status === 403 ? 'That path is outside your library folders.' : 'Could not list this folder.') + '</div>';
                });
            }


            function renderFileList(d) {
                currentFileListing = d;
                deps.view.querySelector('#mdFileCrumbsText').textContent = d.IsRoot
                    ? 'Library folders'
                    : (d.IsRecycleBin ? 'Recycle bin · ' + d.Path
                        : (d.IsLogsDir ? 'Logs · ' + d.Path : d.Path));
                // d.Parent === "" means "one level up is the library-roots pseudo-root". Disable Up only at the pseudo-root itself.
                deps.view.querySelector('#mdFileUp').disabled = d.IsRoot || d.Parent == null;
                deps.view.querySelector('#mdFileUp').setAttribute('data-parent', d.Parent == null ? '' : d.Parent);
                // Recycle bin and logs are read-only shortcuts. Bin restores live on the Recycle bin tab; logs
                // are downloadable but never editable — users pull them for bug reports, never touch them.
                var writeDisabled = d.IsRoot || d.IsRecycleBin || d.IsLogsDir;
                deps.view.querySelector('#mdFileMkdir').disabled = writeDisabled;
                deps.view.querySelector('#mdFileUpload').disabled = writeDisabled;
                deps.view.querySelector('#mdFileUpload').parentElement.style.opacity = writeDisabled ? '.5' : '1';

                var listEl = deps.view.querySelector('#mdFileList');
                if (!d.Entries || d.Entries.length === 0) {
                    listEl.innerHTML = '<div class="mdFileTable"><div class="fieldDescription" style="padding:.7em 1em;">Empty folder.</div></div>';
                    return;
                }

                var sorted = sortEntries(d.Entries, fileSort.by, fileSort.dir);
                if (fileFilter) {
                    var q = fileFilter.toLowerCase();
                    sorted = sorted.filter(function (e) { return e.Name.toLowerCase().indexOf(q) >= 0; });
                }
                var arrow = function (col) {
                    if (fileSort.by !== col) return '';
                    return '<span class="material-icons">' + (fileSort.dir === 'asc' ? 'arrow_drop_up' : 'arrow_drop_down') + '</span>';
                };
                var selectable = !d.IsRoot && !d.IsRecycleBin && !d.IsLogsDir;
                var html = '<div class="mdFileTable"><div class="mdFileHead">' +
                    (selectable ? '<input type="checkbox" id="mdFileSelectAll" title="Select all shown" />' : '<span style="width:1.05em;"></span>') +
                    '<span class="material-icons" style="opacity:0;">folder</span>' +
                    '<span class="mdSortHead" data-col="name" style="flex:1;">Name' + arrow('name') + '</span>' +
                    '<span class="mdSortHead mdFileMeta" data-col="size">Size' + arrow('size') + '</span>' +
                    '<span class="mdSortHead mdFileDate" data-col="date">Modified' + arrow('date') + '</span>' +
                    '<span style="min-width:11em;"></span>' +
                    '</div>';
                if (!sorted.length) {
                    html += '<div class="fieldDescription" style="padding:.7em 1em;">No matches.</div>';
                }
                sorted.forEach(function (e) {
                    var isBinShortcut = e.Kind === 'recycle-bin';
                    var isLogsShortcut = e.Kind === 'logs';
                    var isShortcut = isBinShortcut || isLogsShortcut;
                    var icon = isBinShortcut ? 'delete'
                        : isLogsShortcut ? 'article'
                        : (e.IsDirectory ? 'folder' : (e.Name.match(/\.(mkv|mp4|avi|mov|webm|m4v|ts|wmv)$/i) ? 'movie' : 'insert_drive_file'));
                    var color = isBinShortcut ? '#ec4899'
                        : isLogsShortcut ? '#22d3ee'
                        : (e.IsDirectory ? '#f59e0b' : (e.Name.match(/\.(mkv|mp4|avi|mov|webm|m4v|ts|wmv)$/i) ? '#00a4dc' : '#8b5cf6'));
                    var displayName = isBinShortcut ? 'Recycle bin' : isLogsShortcut ? 'Jellyfin logs' : e.Name;
                    var meta = e.IsDirectory ? '' : deps.bytes(e.SizeBytes);
                    var date = new Date(e.ModifiedUtc).toLocaleDateString();
                    // Selection is by-name and shortcut entries live at root where selection is off anyway,
                    // but be defensive: don't offer to select synthetic entries.
                    var checked = !isShortcut && fileSelection.has(e.Name) ? ' checked' : '';
                    var nameCls = 'mdFileName' + (e.IsDirectory || d.IsRoot ? ' mdFileNameLink' : '');
                    html += '<div class="mdFileRow" data-name="' + deps.esc(e.Name) + '" data-isdir="' + (e.IsDirectory ? '1' : '0') + '" data-kind="' + deps.esc(e.Kind || '') + '">' +
                        (selectable ? '<input type="checkbox" class="mdFileCheck"' + checked + ' />' : '<span style="width:1.05em;"></span>') +
                        '<span class="material-icons" style="color:' + color + ';">' + icon + '</span>' +
                        '<span class="' + nameCls + '">' + deps.esc(displayName) + '</span>' +
                        '<span class="mdFileMeta">' + meta + '</span>' +
                        '<span class="mdFileDate">' + date + '</span>' +
                        '<span class="mdFileActions"></span>' +
                        '</div>';
                });
                html += '</div>';
                listEl.innerHTML = html;

                Array.from(listEl.querySelectorAll('.mdSortHead')).forEach(function (h) {
                    h.addEventListener('click', function () {
                        var col = h.getAttribute('data-col');
                        if (fileSort.by === col) fileSort.dir = fileSort.dir === 'asc' ? 'desc' : 'asc';
                        else { fileSort.by = col; fileSort.dir = 'asc'; }
                        renderFileList(currentFileListing);
                    });
                });

                var selectAllEl = listEl.querySelector('#mdFileSelectAll');
                if (selectAllEl) {
                    var shownNames = sorted.map(function (e) { return e.Name; });
                    var allSelected = shownNames.length > 0 && shownNames.every(function (n) { return fileSelection.has(n); });
                    selectAllEl.checked = allSelected;
                    selectAllEl.addEventListener('change', function () {
                        if (this.checked) shownNames.forEach(function (n) { fileSelection.add(n); });
                        else shownNames.forEach(function (n) { fileSelection.delete(n); });
                        renderFileList(currentFileListing);
                    });
                }

                Array.from(listEl.querySelectorAll('.mdFileRow')).forEach(function (row) {
                    var name = row.getAttribute('data-name');
                    var isDir = row.getAttribute('data-isdir') === '1';
                    var actions = row.querySelector('.mdFileActions');
                    var check = row.querySelector('.mdFileCheck');
                    if (check) {
                        check.addEventListener('click', function (e) { e.stopPropagation(); });
                        check.addEventListener('change', function () {
                            if (this.checked) fileSelection.add(name); else fileSelection.delete(name);
                            updateBulkBar();
                        });
                    }

                    if (d.IsRoot) {
                        // Root row: click the name to enter the library folder. No mutations at root level.
                        row.querySelector('.mdFileName').addEventListener('click', function () { loadFiles(name); });
                        return;
                    }
                    var full = joinPath(d.Path, name);
                    if (isDir) {
                        row.querySelector('.mdFileName').addEventListener('click', function () { loadFiles(full); });
                    }
                    if (d.IsRecycleBin) {
                        // Read-only view: no mutations from here. Restore / empty live on the Recycle bin tab.
                        actions.innerHTML = '';
                    } else if (d.IsLogsDir) {
                        // Logs are downloadable but never editable — grabbing a log for a bug report is the
                        // whole reason the shortcut exists.
                        actions.innerHTML = isDir ? ''
                            : '<button is="emby-button" type="button" class="raised mdFileAct" data-act="download" title="Download"><span class="material-icons">download</span></button>';
                    } else {
                        actions.innerHTML =
                            (isDir ? '' : '<button is="emby-button" type="button" class="raised mdFileAct" data-act="download" title="Download"><span class="material-icons">download</span></button>') +
                            '<button is="emby-button" type="button" class="raised mdFileAct" data-act="rename" title="Rename"><span class="material-icons">drive_file_rename_outline</span></button>' +
                            '<button is="emby-button" type="button" class="raised mdFileAct" data-act="move" title="Move"><span class="material-icons">drive_file_move</span></button>' +
                            '<button is="emby-button" type="button" class="raised mdFileAct" data-act="copy" title="Copy"><span class="material-icons">file_copy</span></button>' +
                            '<button is="emby-button" type="button" class="raised mdFileAct mdFileActDanger" data-act="delete" title="Delete (moves to recycle bin)"><span class="material-icons">delete</span></button>';
                    }
                    Array.from(actions.querySelectorAll('.mdFileAct')).forEach(function (btn) {
                        btn.addEventListener('click', function () { doFileAction(btn.getAttribute('data-act'), full, name, isDir); });
                    });
                });

                updateBulkBar();
            }


            function joinPath(dir, name) {
                var sep = dir.lastIndexOf('\\') > dir.lastIndexOf('/') ? '\\' : (dir.indexOf('/') >= 0 && dir.indexOf('\\') < 0 ? '/' : '\\');
                return dir + (dir.match(/[\\\/]$/) ? '' : sep) + name;
            }


            function updateBulkBar() {
                var bar = deps.view.querySelector('#mdFileBulk');
                var n = fileSelection.size;
                if (n === 0) { bar.style.display = 'none'; return; }
                bar.style.display = 'flex';
                deps.view.querySelector('#mdFileBulkCount').textContent = n;
            }


            function bulkDelete() {
                if (!fileSelection.size) return;
                if (!window.confirm('Send ' + fileSelection.size + ' item(s) to the recycle bin?')) return;
                var names = Array.from(fileSelection);
                var sourcePath = currentFilePath;
                var i = 0, ok = 0, fail = 0;
                function next() {
                    if (i >= names.length) {
                        window.alert('Deleted ' + ok + ' of ' + names.length + (fail ? ' — ' + fail + ' failed.' : '.'));
                        fileSelection.clear();
                        loadFiles(currentFilePath);
                        return;
                    }
                    var full = joinPath(sourcePath, names[i++]);
                    deps.api.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('MediaDash/Files/Delete'),
                        contentType: 'application/json',
                        data: JSON.stringify({ Path: full })
                    }).then(function () { ok++; next(); }, function () { fail++; next(); });
                }
                next();
            }


            function bulkDownload() {
                if (!fileSelection.size) return;
                var token = ApiClient.accessToken();
                Array.from(fileSelection).forEach(function (name) {
                    var full = joinPath(currentFilePath, name);
                    window.open(ApiClient.getUrl('MediaDash/Files/Download') + '?path=' + encodeURIComponent(full) + '&ApiKey=' + encodeURIComponent(token), '_blank');
                });
            }


            function bulkMove() {
                if (!fileSelection.size) return;
                var sourcePath = currentFilePath;
                var names = Array.from(fileSelection);
                openPicker(sourcePath, function (targetDir) {
                    if (!targetDir) return;
                    var i = 0, ok = 0, fail = 0;
                    function next() {
                        if (i >= names.length) {
                            window.alert('Moved ' + ok + ' of ' + names.length + (fail ? ' — ' + fail + ' failed.' : '.'));
                            fileSelection.clear();
                            loadFiles(currentFilePath);
                            return;
                        }
                        var name = names[i++];
                        var from = joinPath(sourcePath, name);
                        var to = joinPath(targetDir, name);
                        deps.api.ajax({
                            type: 'POST',
                            url: ApiClient.getUrl('MediaDash/Files/Move'),
                            contentType: 'application/json',
                            data: JSON.stringify({ From: from, To: to })
                        }).then(function () { ok++; next(); }, function () { fail++; next(); });
                    }
                    next();
                });
            }


            // ---------- Folder picker ----------
            function openPicker(startPath, onPick) {
                picker.open = true;
                picker.onPick = onPick;
                deps.view.querySelector('#mdPicker').style.display = 'flex';
                document.body.style.overflow = 'hidden';
                loadPicker(startPath || '');
            }


            function closePicker() {
                pickerRequest++;
                picker.open = false;
                picker.onPick = null;
                deps.view.querySelector('#mdPicker').style.display = 'none';
                document.body.style.overflow = '';
            }


            function loadPicker(path) {
                var request = ++pickerRequest;
                var listEl = deps.view.querySelector('#mdPickerList');
                listEl.innerHTML = '<div class="fieldDescription" style="padding:.6em .8em;">Loading…</div>';
                // UI-16: clear the previously accepted path and disable Accept until the new
                // listing lands. Without this, clicking Accept while the request is in flight
                // (or after it failed) commits the move/copy to the folder we were navigating
                // AWAY from, silently misrouting the file.
                picker.path = '';
                var acceptBtn = deps.view.querySelector('#mdPickerAccept');
                if (acceptBtn) acceptBtn.disabled = true;
                var url = 'Files/List' + (path ? '?path=' + encodeURIComponent(path) : '');
                deps.api(url).then(function (d) {
                    if (request !== pickerRequest || !picker.open) return;
                    picker.path = d.Path || '';
                    picker.listing = d;
                    renderPicker(d);
                }, function () {
                    if (request !== pickerRequest || !picker.open) return;
                    listEl.innerHTML = '<div class="fieldDescription" style="color:#ef4444;padding:.6em .8em;">Could not list this folder.</div>';
                    // Accept stays disabled — no valid destination to accept to.
                });
            }


            function renderPicker(d) {
                deps.view.querySelector('#mdPickerCrumbsText').textContent = d.IsRoot ? 'Choose a library folder' : d.Path;
                deps.view.querySelector('#mdPickerUp').disabled = d.IsRoot || d.Parent == null;
                deps.view.querySelector('#mdPickerUp').setAttribute('data-parent', d.Parent == null ? '' : d.Parent);
                deps.view.querySelector('#mdPickerAccept').disabled = d.IsRoot;

                var dirs = (d.Entries || []).filter(function (e) { return e.IsDirectory; });
                dirs.sort(function (a, b) { return a.Name.localeCompare(b.Name, undefined, { numeric: true, sensitivity: 'base' }); });
                if (!dirs.length) {
                    deps.view.querySelector('#mdPickerList').innerHTML = '<div class="fieldDescription" style="padding:.7em 1em;">No subfolders.</div>';
                    return;
                }
                var html = '';
                dirs.forEach(function (e) {
                    var target = d.IsRoot ? e.Name : joinPath(d.Path, e.Name);
                    html += '<div class="mdPickerRow" data-target="' + deps.esc(target) + '">' +
                        '<span class="material-icons" style="color:#f59e0b;">folder</span>' +
                        '<span>' + deps.esc(e.Name) + '</span>' +
                        '</div>';
                });
                deps.view.querySelector('#mdPickerList').innerHTML = html;
                Array.from(deps.view.querySelectorAll('.mdPickerRow')).forEach(function (r) {
                    r.addEventListener('click', function () { loadPicker(r.getAttribute('data-target')); });
                });
            }


            function doFileAction(act, full, name, isDir) {
                if (act === 'download') {
                    var tokenParam = ApiClient.accessToken ? '&ApiKey=' + encodeURIComponent(ApiClient.accessToken()) : '';
                    window.open(ApiClient.getUrl('MediaDash/Files/Download') + '?path=' + encodeURIComponent(full) + tokenParam, '_blank');
                    return;
                }
                if (act === 'rename') {
                    var newName = window.prompt('New name for "' + name + '":', name);
                    if (!newName || newName === name) return;
                    fileMutate('Files/Rename', { Path: full, NewName: newName });
                    return;
                }
                if (act === 'move') {
                    openPicker(currentFilePath, function (targetDir) {
                        if (!targetDir) return;
                        fileMutate('Files/Move', { From: full, To: joinPath(targetDir, name) });
                    });
                    return;
                }
                if (act === 'copy') {
                    openPicker(currentFilePath, function (targetDir) {
                        if (!targetDir) return;
                        fileMutate('Files/Copy', { From: full, To: joinPath(targetDir, name) });
                    });
                    return;
                }
                if (act === 'delete') {
                    if (!window.confirm('Send "' + name + '" to the recycle bin?' + (isDir ? ' Everything inside the folder goes with it.' : ''))) return;
                    fileMutate('Files/Delete', { Path: full });
                    return;
                }
            }


            function uploadFiles(fileList) {
                var destinationPath = currentFilePath;
                if (!fileList || !fileList.length || !currentFilePath) return;
                var status = deps.view.querySelector('#mdFileUploadStatus');
                var files = Array.from(fileList);
                var results = { ok: 0, fail: 0, total: files.length };

                function uploadOne(idx) {
                    if (idx >= files.length) {
                        status.textContent = results.fail === 0
                            ? 'Uploaded ' + results.ok + ' file(s).'
                            : ('Uploaded ' + results.ok + ' of ' + results.total + ' — ' + results.fail + ' failed.');
                        loadFiles(currentFilePath);
                        return;
                    }
                    var file = files[idx];
                    var xhr = new XMLHttpRequest();
                    xhr.open('POST', ApiClient.getUrl('MediaDash/Files/Upload') + '?path=' + encodeURIComponent(destinationPath) + '&name=' + encodeURIComponent(file.name));
                    xhr.setRequestHeader('X-Emby-Token', ApiClient.accessToken());
                    xhr.setRequestHeader('Content-Type', 'application/octet-stream');
                    xhr.upload.onprogress = function (e) {
                        if (e.lengthComputable) {
                            status.textContent = 'Uploading ' + file.name + ' (' + (idx + 1) + '/' + files.length + ')… ' + Math.round(e.loaded * 100 / e.total) + '%';
                        }
                    };
                    xhr.onload = function () {
                        if (xhr.status >= 200 && xhr.status < 300) results.ok++;
                        else results.fail++;
                        uploadOne(idx + 1);
                    };
                    xhr.onerror = function () { results.fail++; uploadOne(idx + 1); };
                    xhr.send(file);
                }

                status.textContent = 'Uploading ' + files.length + ' file(s)…';
                uploadOne(0);
            }


            function fileMutate(endpoint, body) {
                deps.api.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('MediaDash/' + endpoint),
                    contentType: 'application/json',
                    data: JSON.stringify(body)
                }).then(function () {
                    loadFiles(currentFilePath);
                }, function (xhr) {
                    // Prefer the server's message body — it's specific ("Jellyfin lacks write access to X…") vs the generic fallback below.
                    var serverMsg = (xhr && xhr.responseText) ? xhr.responseText.replace(/^"|"$/g, '') : '';
                    var msg = serverMsg;
                    if (!msg) {
                        if (xhr && xhr.status === 403) msg = 'Blocked: outside your library folders, or Jellyfin lacks write access.';
                        else if (xhr && xhr.status === 404) msg = 'Not found.';
                        else if (xhr && xhr.status === 409) msg = 'An entry already exists at the destination.';
                        else if (xhr && xhr.status === 400) msg = 'Invalid name (no path separators, no "..").';
                        else msg = 'That operation failed.';
                    }
                    window.alert(msg);
                });
            }


            function bindFiles() {
                    deps.view.querySelector('#mdFileUp').addEventListener('click', function () {
                        var parent = this.getAttribute('data-parent');
                        loadFiles(parent || '');
                    });
                    deps.view.querySelector('#mdFileRefresh').addEventListener('click', function () { loadFiles(currentFilePath); });
                    deps.view.querySelector('#mdFileFilter').addEventListener('input', function () {
                        fileFilter = this.value || '';
                        if (currentFileListing) renderFileList(currentFileListing);
                    });
                    deps.view.querySelector('#mdFileBulkDelete').addEventListener('click', bulkDelete);
                    deps.view.querySelector('#mdFileBulkMove').addEventListener('click', bulkMove);
                    deps.view.querySelector('#mdFileBulkDownload').addEventListener('click', bulkDownload);
                    deps.view.querySelector('#mdFileBulkClear').addEventListener('click', function () {
                        fileSelection.clear();
                        if (currentFileListing) renderFileList(currentFileListing);
                    });
                    deps.view.querySelector('#mdPickerCancel').addEventListener('click', closePicker);
                    deps.view.querySelector('#mdPickerAccept').addEventListener('click', function () {
                        var cb = picker.onPick; var p = picker.path;
                        closePicker();
                        if (cb) cb(p);
                    });
                    deps.view.querySelector('#mdPickerUp').addEventListener('click', function () {
                        var parent = this.getAttribute('data-parent');
                        loadPicker(parent || '');
                    });
                    deps.view.querySelector('#mdFileMkdir').addEventListener('click', function () {
                        if (!currentFilePath) return;
                        var name = window.prompt('New folder name:');
                        if (!name) return;
                        fileMutate('Files/Mkdir', { Path: currentFilePath, Name: name });
                    });
                    deps.view.querySelector('#mdFileUpload').addEventListener('change', function () {
                        uploadFiles(this.files);
                        this.value = '';
                    });

                    var drop = deps.view.querySelector('#mdFileDrop');
                    ['dragenter', 'dragover'].forEach(function (evt) {
                        drop.addEventListener(evt, function (e) {
                            if (!currentFilePath) return;
                            e.preventDefault(); e.stopPropagation();
                            drop.classList.add('mdDropActive');
                        });
                    });
                    ['dragleave', 'dragend'].forEach(function (evt) {
                        drop.addEventListener(evt, function (e) {
                            // Only unhighlight when leaving the outer container, not moving between children.
                            if (e.target === drop) drop.classList.remove('mdDropActive');
                        });
                    });
                    drop.addEventListener('drop', function (e) {
                        e.preventDefault(); e.stopPropagation();
                        drop.classList.remove('mdDropActive');
                        if (!currentFilePath) {
                            deps.view.querySelector('#mdFileUploadStatus').textContent = 'Open a library folder first, then drop files here.';
                            return;
                        }
                        uploadFiles(e.dataTransfer && e.dataTransfer.files);
                    });

                    bindCtxMenu();
            }


            // ---------- Context menu (manual add + protect) ----------
            // Resolved lazily in bindCtxMenu() — deps.view isn't populated until the tab mounts,
            // so touching it at factory time crashes with "Cannot read properties of undefined".
            var ctxMenu, ctxSubmenu, protectDialog;
            var ctxTargetName = '';
            var pressTimer = null;
            var pressOrigin = null;

            function isWritableContext() {
                if (!currentFileListing) return false;
                return !currentFileListing.IsRoot && !currentFileListing.IsRecycleBin && !currentFileListing.IsLogsDir;
            }

            function openCtxMenu(clientX, clientY, name, isDir) {
                if (!isWritableContext()) return;
                ctxTargetName = name;
                // Folders can be protected but not added as an Issue — hide the Issues item.
                ctxMenu.querySelector('[data-ctx="issues"]').style.display = isDir ? 'none' : 'flex';
                ctxSubmenu.style.display = 'none';
                ctxMenu.style.display = 'block';
                // Clamp to viewport so the menu never clips off-screen.
                ctxMenu.style.left = Math.min(clientX, window.innerWidth - 240) + 'px';
                ctxMenu.style.top = Math.min(clientY, window.innerHeight - 180) + 'px';
            }

            function closeCtxMenu() {
                ctxMenu.style.display = 'none';
                ctxSubmenu.style.display = 'none';
                ctxTargetName = '';
            }

            function openProtectDialog(path) {
                deps.view.querySelector('#mdProtectPath').textContent = path;
                deps.view.querySelector('#mdProtectNote').value = '';
                deps.view.querySelector('#mdProtectScanners').value = '*';
                deps.view.querySelector('#mdProtectFixers').value = '*';
                protectDialog.style.display = 'flex';
            }
            function closeProtectDialog() { protectDialog.style.display = 'none'; }

            function submitManualIssue(type) {
                var body = { Path: joinPath(currentFilePath, ctxTargetName), Type: type };
                closeCtxMenu();
                deps.api.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('MediaDash/Issues/Manual'),
                    contentType: 'application/json',
                    data: JSON.stringify(body)
                }).then(function () {
                    showToast('Added as ' + type + '. Switch to the Issues tab to review.');
                }, function (xhr) {
                    extractErrorMessage(xhr, 'Could not add issue.').then(window.alert);
                });
            }

            // ApiClient.ajax rejects with a Response (fetch) OR an XHR depending on Jellyfin version.
            // Return a Promise<string> of the server message, falling back to HTTP status / fallback.
            // ASP.NET returns JSON-encoded strings with ASCII escapes (', —); JSON.parse
            // handles those; regex quote-strip leaves them literal and ugly.
            function extractErrorMessage(xhr, fallback) {
                var status = xhr && xhr.status;
                var statusText = xhr && xhr.statusText;
                var def = status ? ('HTTP ' + status + (statusText ? ': ' + statusText : '')) : (fallback || 'Request failed.');
                var decode = function (body) {
                    if (!body) return def;
                    try { var parsed = JSON.parse(body); if (typeof parsed === 'string') return parsed; } catch (e) {}
                    return body;
                };
                if (!xhr) return Promise.resolve(def);
                if (typeof xhr.text === 'function') {
                    return xhr.text().then(decode, function () { return def; });
                }
                if (xhr.responseText) return Promise.resolve(decode(xhr.responseText));
                return Promise.resolve(def);
            }

            function showToast(msg) {
                // Jellyfin dashboard exposes a global Dashboard.alert / toast helper on most pages. Fall back to
                // window.alert when it isn't available (keeps parity with fileMutate's error path).
                if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
                    window.Dashboard.alert(msg);
                } else {
                    window.alert(msg);
                }
            }

            function bindCtxMenu() {
                ctxMenu = deps.view.querySelector('#mdFileCtxMenu');
                ctxSubmenu = deps.view.querySelector('#mdFileCtxSubmenu');
                protectDialog = deps.view.querySelector('#mdProtectDialog');
                var listEl = deps.view.querySelector('#mdFileList');

                // Desktop right-click, delegated.
                listEl.addEventListener('contextmenu', function (e) {
                    var row = e.target.closest('.mdFileRow');
                    if (!row) return;
                    e.preventDefault();
                    openCtxMenu(e.clientX, e.clientY, row.getAttribute('data-name'), row.getAttribute('data-isdir') === '1');
                });

                // Touch long-press (500ms with <10px drift).
                listEl.addEventListener('touchstart', function (e) {
                    var row = e.target.closest('.mdFileRow');
                    if (!row || e.touches.length !== 1) return;
                    var t = e.touches[0];
                    pressOrigin = { x: t.clientX, y: t.clientY, name: row.getAttribute('data-name'), isDir: row.getAttribute('data-isdir') === '1' };
                    pressTimer = setTimeout(function () {
                        if (!pressOrigin) return;
                        openCtxMenu(pressOrigin.x, pressOrigin.y, pressOrigin.name, pressOrigin.isDir);
                        pressOrigin = null;
                    }, 500);
                });
                function cancelPress() {
                    if (pressTimer) { clearTimeout(pressTimer); pressTimer = null; }
                    pressOrigin = null;
                }
                listEl.addEventListener('touchmove', function (e) {
                    if (!pressOrigin || e.touches.length !== 1) return;
                    var t = e.touches[0];
                    if (Math.abs(t.clientX - pressOrigin.x) > 10 || Math.abs(t.clientY - pressOrigin.y) > 10) cancelPress();
                });
                listEl.addEventListener('touchend', cancelPress);
                listEl.addEventListener('touchcancel', cancelPress);

                // Outside-click / Escape / scroll closes the menu.
                document.addEventListener('click', function (e) {
                    if (ctxMenu.style.display === 'none') return;
                    if (!ctxMenu.contains(e.target)) closeCtxMenu();
                });
                document.addEventListener('keydown', function (e) {
                    if (e.key === 'Escape') closeCtxMenu();
                });
                listEl.addEventListener('scroll', closeCtxMenu, true);

                // "Add to issues" opens the submenu.
                ctxMenu.querySelector('[data-ctx="issues"]').addEventListener('click', function (e) {
                    e.stopPropagation();
                    ctxSubmenu.style.display = 'block';
                });

                // "Protect this path" opens the dialog.
                ctxMenu.querySelector('[data-ctx="protect"]').addEventListener('click', function (e) {
                    e.stopPropagation();
                    var full = joinPath(currentFilePath, ctxTargetName);
                    closeCtxMenu();
                    openProtectDialog(full);
                });

                // Submenu type picks.
                Array.from(ctxSubmenu.querySelectorAll('.mdCtxItem')).forEach(function (item) {
                    item.addEventListener('click', function (e) {
                        e.stopPropagation();
                        submitManualIssue(item.getAttribute('data-type'));
                    });
                });

                // Protect dialog buttons.
                deps.view.querySelector('#mdProtectCancel').addEventListener('click', closeProtectDialog);
                deps.view.querySelector('#mdProtectAccept').addEventListener('click', function () {
                    var path = deps.view.querySelector('#mdProtectPath').textContent;
                    var body = {
                        Kind: 'folder',
                        Target: path,
                        ScannersMask: deps.view.querySelector('#mdProtectScanners').value || '',
                        FixersMask: deps.view.querySelector('#mdProtectFixers').value || '',
                        Note: deps.view.querySelector('#mdProtectNote').value || ''
                    };
                    deps.api.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('MediaDash/Protection'),
                        contentType: 'application/json',
                        data: JSON.stringify(body)
                    }).then(function () {
                        closeProtectDialog();
                        showToast('Protected ' + path + '.');
                    }, function (xhr) {
                        extractErrorMessage(xhr, 'Could not add protection.').then(window.alert);
                    });
                });
            }
        return {
            get currentFilePath() { return currentFilePath; },
            set currentFilePath(value) { currentFilePath = value; },
            get loadFiles() { return loadFiles; },
            get bindFiles() { return bindFiles; }
        };
    };
})();
