﻿//
// GitRepository.cs
//
// Author:
//       Lluis Sanchez Gual <lluis@novell.com>
//
// Copyright (c) 2010 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

//#define DEBUG_GIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using MonoDevelop.Core;
using MonoDevelop.Core.Text;
using MonoDevelop.Ide;
using ProgressMonitor = MonoDevelop.Core.ProgressMonitor;

namespace MonoDevelop.VersionControl.Git
{
	[Flags]
	public enum GitUpdateOptions
	{
		None = 0x0,
		SaveLocalChanges = 0x1,
		UpdateSubmodules = 0x2,
		NormalUpdate = SaveLocalChanges | UpdateSubmodules,
	}

	public sealed class GitRepository : UrlBasedRepository
	{
		LibGit2Sharp.Repository rootRepository;
		internal LibGit2Sharp.Repository RootRepository {
			get { return rootRepository; }
			private set {
				if (rootRepository == value)
					return;

				ShutdownFileWatcher ();
				ShutdownScheduler ();

				if (rootRepository != null)
					rootRepository.Dispose ();

				rootRepository = value;

				InitScheduler ();
				if (this.watchGitLockfiles)
					InitFileWatcher (false);
			}
		}

		public static event EventHandler BranchSelectionChanged;

		FileSystemWatcher watcher;

		readonly bool watchGitLockfiles;

		public GitRepository ()
		{
			Url = "git://";
		}

		internal GitRepository (VersionControlSystem vcs, FilePath path, string url, bool watchGitLockfiles) : base (vcs)
		{
			RootRepository = new LibGit2Sharp.Repository (path);
			RootPath = RootRepository.Info.WorkingDirectory;
			Url = url;
			this.watchGitLockfiles = watchGitLockfiles;

			if (this.watchGitLockfiles && watcher == null)
				InitFileWatcher ();
		}

		public GitRepository (VersionControlSystem vcs, FilePath path, string url) : this (vcs, path, url, true)
		{

		}

		void InitFileWatcher (bool throwIfIndexMissing = true)
		{
			if (RootRepository == null)
				throw new InvalidOperationException ($"{nameof (RootRepository)} not initialized, FileSystemWantcher can not be initialized");
			if (throwIfIndexMissing && RootPath.IsNullOrEmpty)
				throw new InvalidOperationException ($"{nameof (RootPath)} not set, FileSystemWantcher can not be initialized");
			FilePath dotGitPath = RootRepository.Info.Path;
			if (!dotGitPath.IsDirectory || !Directory.Exists (dotGitPath)) {
				if (!throwIfIndexMissing)
					return;
				throw new InvalidOperationException ($"{nameof (RootPath)} is not a valid Git repository, FileSystemWantcher can not be initialized");
			}

			if (watcher?.Path == dotGitPath.CanonicalPath.ParentDirectory)
				return;

			ShutdownFileWatcher ();

			watcher = new FileSystemWatcher (dotGitPath.CanonicalPath.ParentDirectory, Path.Combine (dotGitPath.FileName, "*"));
			watcher.Created += HandleGitLockCreated;
			watcher.Deleted += HandleGitLockDeleted;
			watcher.Renamed += HandleGitLockRenamed;
			watcher.EnableRaisingEvents = true;
		}

		void ShutdownFileWatcher ()
		{
			if (watcher != null) {
				watcher.EnableRaisingEvents = false;
				watcher.Dispose ();
				watcher = null;
			}
		}

		const string rebaseApply = "rebase-apply";
		const string rebaseMerge = "rebase-merge";
		const string cherryPickHead = "CHERRY_PICK_HEAD";
		const string revertHead = "REVERT_HEAD";

		static bool ShouldLock (string fullPath)
		{
			var fileName = Path.GetFileName (fullPath);
			return fileName == rebaseApply || fileName == rebaseMerge || fileName == cherryPickHead || fileName == revertHead;
		}

		void HandleGitLockCreated (object sender, FileSystemEventArgs e)
		{
			if (e.FullPath.EndsWith (".lock", StringComparison.Ordinal))
				OnGitLocked ();
			if (ShouldLock (e.FullPath))
				OnGitLocked ();
		}

		void HandleGitLockRenamed (object sender, RenamedEventArgs e)
		{
			if (e.OldName.EndsWith (".lock", StringComparison.Ordinal) && !e.Name.EndsWith (".lock", StringComparison.Ordinal))
				OnGitUnlocked ();
			if (ShouldLock (e.OldName))
				OnGitUnlocked ();
		}

		void HandleGitLockDeleted (object sender, FileSystemEventArgs e)
		{
			if (e.FullPath.EndsWith (".lock", StringComparison.Ordinal))
				OnGitUnlocked ();
			if (ShouldLock (e.FullPath))
				OnGitUnlocked ();
		}

		readonly ManualResetEvent gitLock = new ManualResetEvent (true);

		void OnGitLocked ()
		{
			gitLock.Reset ();
			FileService.FreezeEvents ();
		}

		void OnGitUnlocked ()
		{
			gitLock.Set ();
			ThawEvents ();
		}

		bool WaitAndFreezeEvents (CancellationToken cancellationToken)
		{
			WaitHandle.WaitAny (new WaitHandle [] { gitLock, cancellationToken.WaitHandle });
			if (cancellationToken.IsCancellationRequested)
				return false;

			FileService.FreezeEvents ();
			return true;
		}

		void ThawEvents ()
		{
			FileService.ThawEvents ();
		}

		protected override void Dispose (bool disposing)
		{
			if (IsDisposed)
				return;
			var opfactory = ExclusiveOperationFactory; // cache the factory, otherwise it will throw once IsDisposed is true
			// ensure that no new operations can be started while we wait for the scheduler to shutdown
			IsDisposed = true;

			if (disposing) {
				ShutdownFileWatcher ();
				opfactory.StartNew (() => {
					try {
						rootRepository?.Dispose ();
					} catch (Exception e) {
						LoggingService.LogInternalError ("Disposing LibGit2Sharp.Repository failed", e);
					}
					if (cachedSubmodules != null) {
						foreach (var submodule in cachedSubmodules) {
							if (submodule?.Item2 != null) {
								try {
									submodule?.Item2.Dispose ();
								} catch (Exception e) {
									LoggingService.LogInternalError ("Disposing LibGit2Sharp.Repository failed", e);
								}
							}
						}
					}
				}).Ignore ();
			}

			// now it's safe to dispose the base and release all information caches
			// this will also wait for the scheduler to finish all operations and shutdown
			base.Dispose (disposing);

			watcher = null;
			rootRepository = null;
			cachedSubmodules = null;
		}

		public override string[] SupportedProtocols {
			get {
				return new [] {"git", "ssh", "http", "https", /*"ftp", "ftps", "rsync",*/ "file"};
			}
		}

		public override bool IsUrlValid (string url)
		{
			if (url.Contains (':')) {
				var tokens = url.Split (new[] { ':' }, 2);
				if (Uri.IsWellFormedUriString (tokens [0], UriKind.RelativeOrAbsolute) ||
					Uri.IsWellFormedUriString (tokens [1], UriKind.RelativeOrAbsolute))
					return true;
			}

			return base.IsUrlValid (url);
		}

		/*public override string[] SupportedNonUrlProtocols {
			get {
				return new string[] {"ssh/scp"};
			}
		}

		public override string Protocol {
			get {
				string p = base.Protocol;
				if (p != null)
					return p;
				return IsUrlValid (Url) ? "ssh/scp" : null;
			}
		}*/

		public override void CopyConfigurationFrom (Repository other)
		{
			base.CopyConfigurationFrom (other);

			var r = (GitRepository)other;
			RootPath = r.RootPath;
			if (!RootPath.IsNullOrEmpty)
				RootRepository = new LibGit2Sharp.Repository (RootPath);
		}

		public override string LocationDescription {
			get { return Url ?? RootPath; }
		}

		public override bool AllowLocking {
			get { return false; }
		}

		public override Task<string> GetBaseTextAsync (FilePath localFile, CancellationToken cancellationToken)
		{
			return RunOperationAsync (localFile, repository => {
				var c = GetHeadCommit (repository);
				return c == null ? string.Empty : GetCommitTextContent (c, localFile, repository);
			}, cancellationToken: cancellationToken);
		}

		static Commit GetHeadCommit (LibGit2Sharp.Repository repository)
		{
			return repository.Head.Tip;
		}

		public StashCollection GetStashes ()
		{
			return RunOperation (() => RootRepository.Stashes);
		}

		const CheckoutNotifyFlags refreshFlags = CheckoutNotifyFlags.Updated | CheckoutNotifyFlags.Conflict | CheckoutNotifyFlags.Untracked | CheckoutNotifyFlags.Dirty;
		bool RefreshFile (string path, CheckoutNotifyFlags flags)
		{
			return RefreshFile (RootRepository, path, flags);
		}

		bool RefreshFile (LibGit2Sharp.Repository repository, string path, CheckoutNotifyFlags flags)
		{
			FilePath fp = repository.FromGitPath (path);
			Gtk.Application.Invoke ((o, args) => {
				if (IdeApp.IsInitialized) {
					MonoDevelop.Ide.Gui.Document doc = IdeApp.Workbench.GetDocument (fp);
					if (doc != null)
						doc.Reload ();
				}
				VersionControlService.NotifyFileStatusChanged (new FileUpdateEventArgs (this, fp, false));
			});
			return true;
		}

		const int progressThrottle = 200;
		static System.Diagnostics.Stopwatch throttleWatch = new System.Diagnostics.Stopwatch ();
		static bool OnTransferProgress (TransferProgress tp, ProgressMonitor monitor, ref int progress)
		{
			if (progress == 0 && tp.ReceivedObjects == 0) {
				progress = 1;
				monitor.Log.WriteLine (GettextCatalog.GetString ("Receiving and indexing objects"), 2 * tp.TotalObjects);
				throttleWatch.Restart ();
			}

			int currentProgress = tp.ReceivedObjects + tp.IndexedObjects;
			int steps = currentProgress - progress;
			if (throttleWatch.ElapsedMilliseconds > progressThrottle) {
				monitor.Step (steps);
				throttleWatch.Restart ();
				progress = currentProgress;
			}

			if (tp.IndexedObjects >= tp.TotalObjects) {
				throttleWatch.Stop ();
			}

			return !monitor.CancellationToken.IsCancellationRequested;
		}

		static void OnCheckoutProgress (int completedSteps, int totalSteps, ProgressMonitor monitor, ref int progress)
		{
			if (progress == 0 && completedSteps == 0) {
				progress = 1;
				monitor.Log.WriteLine (GettextCatalog.GetString ("Checking out files"), 2 * totalSteps);
				throttleWatch.Restart ();
			}

			int steps = completedSteps - progress;
			if (throttleWatch.ElapsedMilliseconds > progressThrottle) {
				monitor.Step (steps);
				throttleWatch.Restart ();
				progress = completedSteps;
			}

			if (completedSteps >= totalSteps) {
				throttleWatch.Stop ();
			}
		}

		public StashApplyStatus ApplyStash (ProgressMonitor monitor, int stashIndex)
		{
			return RunBlockingOperation (() => ApplyStash (RootRepository, monitor, stashIndex), true, monitor.CancellationToken);
		}

		StashApplyStatus ApplyStash (LibGit2Sharp.Repository repository, ProgressMonitor monitor, int stashIndex)
		{
			if (monitor != null)
				monitor.BeginTask (GettextCatalog.GetString ("Applying stash"), 1);

			int progress = 0;
			StashApplyStatus res = repository.Stashes.Apply (stashIndex, new StashApplyOptions {
				CheckoutOptions = new CheckoutOptions {
					OnCheckoutProgress = (path, completedSteps, totalSteps) => OnCheckoutProgress (completedSteps, totalSteps, monitor, ref progress),
					OnCheckoutNotify = (string path, CheckoutNotifyFlags flags) => RefreshFile (repository, path, flags),
					CheckoutNotifyFlags = refreshFlags,
				},
			});

			if (monitor != null)
				monitor.EndTask ();

			return res;
		}
		public override bool TryGetFileUpdateEventInfo (Repository rep, FilePath file, out FileUpdateEventInfo eventInfo)
		{
			if (file.FileName == "index" && file.ParentDirectory.FileName == ".git") {
				eventInfo = FileUpdateEventInfo.UpdateRepository (rep);
				return true;
			}
			return base.TryGetFileUpdateEventInfo (rep, file, out eventInfo);
		}

		public StashApplyStatus PopStash (ProgressMonitor monitor, int stashIndex)
		{
			if (monitor != null)
				monitor.BeginTask (GettextCatalog.GetString ("Popping stash"), 1);

			var res = RunBlockingOperation (() => {
				var stash = RootRepository.Stashes [stashIndex];
				int progress = 0;
				return RootRepository.Stashes.Pop (stashIndex, new StashApplyOptions {
					CheckoutOptions = new CheckoutOptions {
						OnCheckoutProgress = (path, completedSteps, totalSteps) => OnCheckoutProgress (completedSteps, totalSteps, monitor, ref progress),
						OnCheckoutNotify = (string path, CheckoutNotifyFlags flags) => RefreshFile (path, flags),
						CheckoutNotifyFlags = refreshFlags,
					},
				});
			}, true, monitor.CancellationToken);

			if (monitor != null)
				monitor.EndTask ();

			return res;
		}

		public bool TryCreateStash (ProgressMonitor monitor, string message, out Stash stash)
		{
			Signature sig = GetSignature ();
			stash = null;
			if (sig == null)
				return false;

			if (monitor != null)
				monitor.BeginTask (GettextCatalog.GetString ("Stashing changes"), 1);

			stash = RunBlockingOperation (() => RootRepository.Stashes.Add (sig, message, StashModifiers.Default | StashModifiers.IncludeUntracked), cancellationToken: monitor.CancellationToken);

			if (monitor != null)
				monitor.EndTask ();
			return true;
		}

		internal Signature GetSignature()
		{
			// TODO: Investigate Configuration.BuildSignature.
			string name;
			string email;

			GetUserInfo (out name, out email);
			if (name == null || email == null)
				return null;

			return new Signature (name, email, DateTimeOffset.Now);
		}

		DateTime cachedSubmoduleTime = DateTime.MinValue;
		Tuple<FilePath, LibGit2Sharp.Repository> [] cachedSubmodules = new Tuple<FilePath, LibGit2Sharp.Repository> [0];
		Tuple<FilePath, LibGit2Sharp.Repository> [] GetCachedSubmodules ()
		{
			var submoduleWriteTime = File.GetLastWriteTimeUtc (RootPath.Combine (".gitmodules"));
			if (cachedSubmoduleTime != submoduleWriteTime) {
				cachedSubmoduleTime = submoduleWriteTime;
				lock (this) {
					cachedSubmodules = RootRepository.Submodules.Select (s => {
						var fp = new FilePath (Path.Combine (RootRepository.Info.WorkingDirectory, s.Path.Replace ('/', Path.DirectorySeparatorChar))).CanonicalPath;
						return new Tuple<FilePath, LibGit2Sharp.Repository> (fp, new LibGit2Sharp.Repository (fp));
					}).ToArray ();
				}
			}
			return cachedSubmodules;
		}

		void EnsureBackgroundThread ()
		{
			if (Runtime.IsMainThread)
				throw new InvalidOperationException ("Deadlock prevention: this shall not run on the UI thread");
		}

		void EnsureInitialized ()
		{
			if (IsDisposed)
				throw new ObjectDisposedException (typeof (GitRepository).Name);
			if (RootRepository != null)
				InitFileWatcher ();
		}

		internal void RunSafeOperation (Action action)
		{
			EnsureInitialized ();
			action ();
		}

		internal T RunSafeOperation<T> (Func<T> action)
		{
			EnsureInitialized ();
			return action ();
		}

		internal void RunOperation (FilePath localPath, Action<LibGit2Sharp.Repository> action, bool hasUICallbacks = false)
		{
			EnsureInitialized ();
			if (hasUICallbacks)
				EnsureBackgroundThread ();
			ExclusiveOperationFactory.StartNew (() => action (GetRepository (localPath))).RunWaitAndCapture ();
		}

		internal void RunOperation (Action action, bool hasUICallbacks = false)
		{
			EnsureInitialized ();
			if (hasUICallbacks)
				EnsureBackgroundThread ();
			ExclusiveOperationFactory.StartNew (action).RunWaitAndCapture ();
		}

		internal Task RunOperationAsync (Action action, bool hasUICallbacks = false)
		{
			EnsureInitialized ();
			if (hasUICallbacks)
				EnsureBackgroundThread ();
			return ExclusiveOperationFactory.StartNew (action);
		}

		internal T RunOperation<T> (Func<T> action, bool hasUICallbacks = false)
		{
			EnsureInitialized ();
			if (hasUICallbacks)
				EnsureBackgroundThread ();
			return ExclusiveOperationFactory.StartNew (action).RunWaitAndCapture ();
		}

		internal Task<T> RunOperationAsync<T> (Func<T> action, bool hasUICallbacks = false, CancellationToken cancellationToken = default)
		{
			EnsureInitialized ();
			if (hasUICallbacks)
				EnsureBackgroundThread ();
			return ExclusiveOperationFactory.StartNew (action, cancellationToken);
		}

		internal T RunOperation<T> (FilePath localPath, Func<LibGit2Sharp.Repository, T> action, bool hasUICallbacks = false)
		{
			EnsureInitialized ();
			if (hasUICallbacks)
				EnsureBackgroundThread ();
			return ExclusiveOperationFactory.StartNew (() => action (GetRepository (localPath))).RunWaitAndCapture ();
		}

		internal Task<T> RunOperationAsync<T> (FilePath localPath, Func<LibGit2Sharp.Repository, T> action, bool hasUICallbacks = false, CancellationToken cancellationToken = default)
		{
			EnsureInitialized ();
			if (hasUICallbacks)
				EnsureBackgroundThread ();
			return ExclusiveOperationFactory.StartNew (() => action (GetRepository (localPath)), cancellationToken);
		}

		internal void RunBlockingOperation (Action action, bool hasUICallbacks = false, CancellationToken cancellationToken = default)
		{
			if (hasUICallbacks)
				EnsureBackgroundThread ();
			if (!WaitAndFreezeEvents (cancellationToken))
				return;
			try {

				ExclusiveOperationFactory.StartNew (action).RunWaitAndCapture ();
			} finally {
				ThawEvents ();
			}
		}

		internal void RunBlockingOperation (FilePath localPath, Action<LibGit2Sharp.Repository> action, bool hasUICallbacks = false, CancellationToken cancellationToken = default)
		{
			EnsureInitialized ();
			if (hasUICallbacks)
				EnsureBackgroundThread ();
			if (!WaitAndFreezeEvents (cancellationToken))
				return;
			try {
				ExclusiveOperationFactory.StartNew (() => action (GetRepository (localPath))).RunWaitAndCapture ();
			} finally {
				ThawEvents ();
			}
		}

		internal T RunBlockingOperation<T> (Func<T> action, bool hasUICallbacks = false, CancellationToken cancellationToken = default)
		{
			EnsureInitialized ();
			if (hasUICallbacks)
				EnsureBackgroundThread ();
			if (!WaitAndFreezeEvents (cancellationToken))
				return default;
			try {
				return ExclusiveOperationFactory.StartNew (action).RunWaitAndCapture ();
			} finally {
				ThawEvents ();
			}
		}

		internal T RunBlockingOperation<T> (FilePath localPath, Func<LibGit2Sharp.Repository, T> action, bool hasUICallbacks = false, CancellationToken cancellationToken = default)
		{
			EnsureInitialized ();
			if (hasUICallbacks)
				EnsureBackgroundThread ();
			if (!WaitAndFreezeEvents (cancellationToken))
				return default;
			try {
				return ExclusiveOperationFactory.StartNew (() => action (GetRepository (localPath))).RunWaitAndCapture ();
			} finally {
				ThawEvents ();
			}
		}

		LibGit2Sharp.Repository GetRepository (FilePath localPath)
		{
			return GroupByRepository (new [] { localPath }).First ().Key;
		}

		FilePath GetRepositoryRoot (FilePath localPath)
		{
			return GroupByRepositoryRoot (new [] { localPath }).First ().Key;
		}

		IEnumerable<IGrouping<FilePath, FilePath>> GroupByRepositoryRoot (IEnumerable<FilePath> files)
		{
			var cache = GetCachedSubmodules ();
			return files.GroupBy (f => {
				var res = cache.FirstOrDefault (s => f.IsChildPathOf (s.Item1) || f.FullPath == s.Item1);
				return res != null ? res.Item1 : RootPath;
			});
		}

		IEnumerable<IGrouping<LibGit2Sharp.Repository, FilePath>> GroupByRepository (IEnumerable<FilePath> files)
		{
			var cache = GetCachedSubmodules ();
			return files.GroupBy (f => {
				var res = cache.FirstOrDefault (s => f.IsChildPathOf (s.Item1) || f.FullPath == s.Item1);
				return res != null ? res.Item2 : RootRepository;
			});
		}

		protected override Task<Revision []> OnGetHistoryAsync (FilePath localFile, Revision since, CancellationToken cancellationToken)
		{
			return RunOperationAsync (() => {
				var hc = GetHeadCommit (RootRepository);
				if (hc == null)
					return new GitRevision [0];

				var sinceRev = since != null ? ((GitRevision)since).GetCommit (RootRepository) : null;
				IEnumerable<Commit> commits = RootRepository.Commits.QueryBy (new CommitFilter { SortBy = CommitSortStrategies.Topological });
				if (localFile.CanonicalPath != RootPath.CanonicalPath.ResolveLinks ()) {
					var localPath = RootRepository.ToGitPath (localFile);
					commits = commits.Where (c => {
						int count = c.Parents.Count ();
						if (count > 1)
							return false;

						var localTreeEntry = c.Tree [localPath];
						if (localTreeEntry == null)
							return false;

						if (count == 0)
							return true;

						var parentTreeEntry = c.Parents.Single ().Tree [localPath];
						return parentTreeEntry == null || localTreeEntry.Target.Id != parentTreeEntry.Target.Id;
					});
				}

				return commits.TakeWhile (c => c != sinceRev).Select (commit => {
					var author = commit.Author;
					var shortMessage = commit.MessageShort;
					if (shortMessage.Length > 50) {
						shortMessage = shortMessage.Substring (0, 50) + "…";
					}

					var rev = new GitRevision (this, RootRepository.Info.WorkingDirectory, commit, author.When.LocalDateTime, author.Name, commit.Message) {
						Email = author.Email,
						ShortMessage = shortMessage,
						FileForChanges = localFile,
					};
					return rev;
				}).Cast<Revision> ().ToArray();
			}, cancellationToken: cancellationToken);
		}

		protected override Task<RevisionPath []> OnGetRevisionChangesAsync (Revision revision, CancellationToken cancellationToken = default)
		{
			var rev = (GitRevision)revision;
			return RunOperationAsync (() => {
				var commit = rev.GetCommit (RootRepository);
				if (commit == null)
					return new RevisionPath [0];

				var paths = new List<RevisionPath> ();
				var parent = commit.Parents.FirstOrDefault ();
				var changes = RootRepository.Diff.Compare<TreeChanges> (parent?.Tree, commit.Tree);

				foreach (var entry in changes.Added) {
					cancellationToken.ThrowIfCancellationRequested ();
					paths.Add (new RevisionPath (RootRepository.FromGitPath (entry.Path), RevisionAction.Add, null));
				}
				foreach (var entry in changes.Copied) {
					cancellationToken.ThrowIfCancellationRequested ();
					paths.Add (new RevisionPath (RootRepository.FromGitPath (entry.Path), RevisionAction.Add, null));
				}
				foreach (var entry in changes.Deleted) {
					cancellationToken.ThrowIfCancellationRequested ();
					paths.Add (new RevisionPath (RootRepository.FromGitPath (entry.OldPath), RevisionAction.Delete, null));
				}
				foreach (var entry in changes.Renamed) {
					cancellationToken.ThrowIfCancellationRequested ();
					paths.Add (new RevisionPath (RootRepository.FromGitPath (entry.Path), RootRepository.FromGitPath (entry.OldPath), RevisionAction.Replace, null));
				}
				foreach (var entry in changes.Modified) {
					cancellationToken.ThrowIfCancellationRequested ();
					paths.Add (new RevisionPath (RootRepository.FromGitPath (entry.Path), RevisionAction.Modify, null));
				}
				foreach (var entry in changes.TypeChanged) {
					cancellationToken.ThrowIfCancellationRequested ();
					paths.Add (new RevisionPath (RootRepository.FromGitPath (entry.Path), RevisionAction.Modify, null));
				}
				return paths.ToArray ();
			}, cancellationToken: cancellationToken);
		}


		protected override async Task<IReadOnlyList<VersionInfo>> OnGetVersionInfoAsync (IEnumerable<FilePath> paths, bool getRemoteStatus, CancellationToken cancellationToken)
		{
			try {
				return await GetDirectoryVersionInfoAsync (FilePath.Null, paths, getRemoteStatus, false, cancellationToken);
			} catch (Exception e) {
				LoggingService.LogError ("Failed to query git status", e);
				return paths.Select (x => VersionInfo.CreateUnversioned (x, false)).ToList ();
			}
		}

		protected override async Task<VersionInfo []> OnGetDirectoryVersionInfoAsync (FilePath localDirectory, bool getRemoteStatus, bool recursive, CancellationToken cancellationToken)
		{
			try {
				return await GetDirectoryVersionInfoAsync (localDirectory, null, getRemoteStatus, recursive, cancellationToken);
			} catch (Exception e) {
				LoggingService.LogError ("Failed to get git directory status", e);
				return new VersionInfo [0];
			}
		}

		class RepositoryContainer : IDisposable
		{
			Dictionary<FilePath, LibGit2Sharp.Repository> repositories = new Dictionary<FilePath, LibGit2Sharp.Repository> ();

			public LibGit2Sharp.Repository GetRepository (FilePath root)
			{
				if (!repositories.TryGetValue (root, out var repo) || repo == null) {
					repo = repositories [root] = new LibGit2Sharp.Repository (root);
				}
				return repo;
			}

			bool disposed;
			public void Dispose ()
			{
				if (disposed)
					return;
				foreach (var repo in repositories)
					repo.Value.Dispose ();
				repositories.Clear ();
				repositories = null;
				disposed = true;
			}
		}

		// Used for checking if we will dupe data.
		// This way we reduce the number of GitRevisions created and RevWalks done.
		Dictionary<FilePath, GitRevision> versionInfoCacheRevision = new Dictionary<FilePath, GitRevision> ();
		Dictionary<FilePath, GitRevision> versionInfoCacheEmptyRevision = new Dictionary<FilePath, GitRevision> ();
		async Task<VersionInfo[]> GetDirectoryVersionInfoAsync (FilePath localDirectory, IEnumerable<FilePath> localFileNames, bool getRemoteStatus, bool recursive, CancellationToken cancellationToken)
		{
			var versions = new List<VersionInfo> ();

			if (localFileNames != null) {
				var localFiles = new List<FilePath> ();
				var groups = GroupByRepository (localFileNames);
				foreach (var group in groups) {
					var repositoryRoot = group.Key.Info.WorkingDirectory;
					GitRevision arev;
					if (!versionInfoCacheEmptyRevision.TryGetValue (repositoryRoot, out arev)) {
						arev = new GitRevision (this, repositoryRoot, null);
						versionInfoCacheEmptyRevision.Add (repositoryRoot, arev);
					}
					foreach (var p in group) {
						if (Directory.Exists (p)) {
							if (recursive)
								versions.AddRange (await GetDirectoryVersionInfoAsync (p, getRemoteStatus, true, cancellationToken));
							versions.Add (new VersionInfo (p, "", true, VersionStatus.Versioned, arev, VersionStatus.Versioned, null));
						} else
							localFiles.Add (p);
					}
				}
				// No files to check, we are done
				if (localFiles.Count != 0) {
					foreach (var group in groups) {
						var repository = group.Key;
						var repositoryRoot = repository.Info.WorkingDirectory;

						GitRevision rev = null;
						Commit headCommit = GetHeadCommit (repository);
						if (headCommit != null) {
							if (!versionInfoCacheRevision.TryGetValue (repositoryRoot, out rev)) {
								rev = new GitRevision (this, repositoryRoot, headCommit);
								versionInfoCacheRevision.Add (repositoryRoot, rev);
							} else if (rev.GetCommit (repository) != headCommit) {
								rev = new GitRevision (this, repositoryRoot, headCommit);
								versionInfoCacheRevision [repositoryRoot] = rev;
							}
						}

						GetFilesVersionInfoCore (repository, rev, group.ToList (), versions);
					}
				}
			} else {
				var directories = new List<FilePath> ();
				CollectFiles (directories, localDirectory, recursive);

				// Set directory items as Versioned.
				GitRevision arev = null;
				foreach (var group in GroupByRepositoryRoot (directories)) {
					if (!versionInfoCacheEmptyRevision.TryGetValue (group.Key, out arev)) {
						arev = new GitRevision (this, group.Key, null);
						versionInfoCacheEmptyRevision.Add (group.Key, arev);
					}
					foreach (var p in group)
						versions.Add (new VersionInfo (p, "", true, VersionStatus.Versioned, arev, VersionStatus.Versioned, null));
				}

				var rootRepository = GetRepository (RootPath);
				Commit headCommit = GetHeadCommit (rootRepository);
				if (headCommit != null) {
					if (!versionInfoCacheRevision.TryGetValue (RootPath, out arev)) {
						arev = new GitRevision (this, RootPath, headCommit);
						versionInfoCacheRevision.Add (RootPath, arev);
					} else if (arev.GetCommit (rootRepository) != headCommit) {
						arev = new GitRevision (this, RootPath, headCommit);
						versionInfoCacheRevision [RootPath] = arev;
					}
				}

				GetDirectoryVersionInfoCore (rootRepository, arev, localDirectory.CanonicalPath, versions, recursive);
			}

			return versions.ToArray ();
		}

		static void GetFilesVersionInfoCore (LibGit2Sharp.Repository repo, GitRevision rev, List<FilePath> localPaths, List<VersionInfo> versions)
		{
			foreach (var localPath in localPaths) {
				if (!localPath.IsDirectory) {
					var file = repo.ToGitPath (localPath);
					var status = repo.RetrieveStatus (file);
					AddStatus (repo, rev, file, versions, status, null);
				}
			}
		}

		static void AddStatus (LibGit2Sharp.Repository repo, GitRevision rev, string file, List<VersionInfo> versions, FileStatus status, string directoryPath)
		{
			VersionStatus fstatus = VersionStatus.Versioned;

			if (status != FileStatus.Unaltered) {
				if ((status & FileStatus.NewInIndex) != 0)
					fstatus |= VersionStatus.ScheduledAdd;
				else if ((status & (FileStatus.DeletedFromIndex | FileStatus.DeletedFromWorkdir)) != 0)
					fstatus |= VersionStatus.ScheduledDelete;
				else if ((status & (FileStatus.TypeChangeInWorkdir | FileStatus.TypeChangeInIndex | FileStatus.ModifiedInWorkdir | FileStatus.ModifiedInIndex)) != 0)
					fstatus |= VersionStatus.Modified;
				else if ((status & (FileStatus.RenamedInIndex | FileStatus.RenamedInWorkdir)) != 0)
					fstatus |= VersionStatus.ScheduledReplace;
				else if ((status & (FileStatus.Nonexistent | FileStatus.NewInWorkdir)) != 0)
					fstatus = VersionStatus.Unversioned;
				else if ((status & FileStatus.Ignored) != 0)
					fstatus = VersionStatus.Ignored;
			}

			if (repo.Index.Conflicts [file] != null)
				fstatus = VersionStatus.Versioned | VersionStatus.Conflicted;

			var versionPath = repo.FromGitPath (file);
			if (directoryPath != null && versionPath.ParentDirectory != directoryPath) {
				return;
			}

			versions.Add (new VersionInfo (versionPath, "", false, fstatus, rev, fstatus == VersionStatus.Ignored ? VersionStatus.Unversioned : VersionStatus.Versioned, null));
		}

		static void GetDirectoryVersionInfoCore (LibGit2Sharp.Repository repo, GitRevision rev, FilePath directory, List<VersionInfo> versions, bool recursive)
		{
			var relativePath = repo.ToGitPath (directory);
			var status = repo.RetrieveStatus (new StatusOptions {
				DisablePathSpecMatch = true,
				PathSpec = relativePath != "." ? new [] { relativePath } : new string[0],
				IncludeUnaltered = true,
			});

			foreach (var statusEntry in status) {
				AddStatus (repo, rev, statusEntry.FilePath, versions, statusEntry.State, recursive ? null : directory);
			}
		}

		protected internal override async Task<VersionControlOperation> GetSupportedOperationsAsync (VersionInfo vinfo, CancellationToken cancellationToken)
		{
			VersionControlOperation ops = await base.GetSupportedOperationsAsync (vinfo, cancellationToken);
			if (await GetCurrentRemoteAsync (cancellationToken) == null)
				ops &= ~VersionControlOperation.Update;
			if (vinfo.IsVersioned && !vinfo.IsDirectory)
				ops |= VersionControlOperation.Annotate;
			if (!vinfo.IsVersioned && vinfo.IsDirectory)
				ops &= ~VersionControlOperation.Add;
			return ops;
		}

		static void CollectFiles (List<FilePath> directories, FilePath dir, bool recursive)
		{
			if (!Directory.Exists (dir))
				return;

			directories.AddRange (Directory.GetDirectories (dir, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
				.Select (f => new FilePath (f)));
		}

		protected override async Task<Repository> OnPublishAsync (string serverPath, FilePath localPath, FilePath[] files, string message, ProgressMonitor monitor)
		{
			// Initialize the repository
			RootPath = localPath;
			RootRepository = new LibGit2Sharp.Repository (LibGit2Sharp.Repository.Init (localPath));
			RootRepository.Network.Remotes.Add ("origin", Url);

			// Add the project files
			ChangeSet cs = CreateChangeSet (localPath);
			foreach (FilePath fp in files) {
				LibGit2Sharp.Commands.Stage (RootRepository, RootRepository.ToGitPath (fp));
				await cs.AddFileAsync (fp);
			}

			// Create the initial commit
			cs.GlobalComment = message;
			await CommitAsync (cs, monitor);

			RootRepository.Branches.Update (RootRepository.Branches ["master"], branch => branch.TrackedBranch = "refs/remotes/origin/master");

			RetryUntilSuccess (monitor, credType => {

				try {
					RootRepository.Network.Push (RootRepository.Head, new PushOptions {
						OnPushStatusError = delegate (PushStatusError e) {
							throw new VersionControlException (e.Message);
						},
						CredentialsProvider = (url, userFromUrl, types) => GitCredentials.TryGet (url, userFromUrl, types, credType)
					});
				} catch(VersionControlException vcex) {
					RootRepository.Dispose ();
					RootRepository = null;
					if (RootPath.Combine (".git").IsDirectory)
						Directory.Delete (RootPath.Combine (".git"), true);
					LoggingService.LogError ("Failed to publish to the repository", vcex);
					throw;
				}
			});

			return this;
		}

		protected override async Task OnUpdateAsync (FilePath [] localPaths, bool recurse, ProgressMonitor monitor)
		{
			// TODO: Make it work differently for submodules.
			monitor.BeginTask (GettextCatalog.GetString ("Updating"), 5);

			if (RootRepository.Head.IsTracking) {
				Fetch (monitor, RootRepository.Head.RemoteName);

				GitUpdateOptions options = GitService.StashUnstashWhenUpdating ? GitUpdateOptions.NormalUpdate : GitUpdateOptions.UpdateSubmodules;
				if (GitService.UseRebaseOptionWhenPulling)
					await RebaseAsync (RootRepository.Head.TrackedBranch.FriendlyName, options, monitor, true);
				else
					await MergeAsync (RootRepository.Head.TrackedBranch.FriendlyName, options, monitor, true);

				monitor.Step (1);
			}

			monitor.EndTask ();
		}

		static bool HandleAuthenticationException (AuthenticationException e)
		{
			var ret = MessageService.AskQuestion (
								GettextCatalog.GetString ("Remote server error: {0}", e.Message),
								GettextCatalog.GetString ("Retry authentication?"),
								AlertButton.Yes, AlertButton.No);
			return ret == AlertButton.Yes;
		}

		static void RetryUntilSuccess (ProgressMonitor monitor, Action<GitCredentialsType> action, Action onRetry = null)
			=> RetryUntilSuccessAsync (monitor, gct => { action (gct); return Task.CompletedTask; }, onRetry).Ignore ();

		static async Task RetryUntilSuccessAsync (ProgressMonitor monitor, Func<GitCredentialsType, Task> func, Action onRetry = null)
		{
			bool retry;
			using (var tfsSession = new TfsSmartSession ()) {
				do {
					var credType = tfsSession.Disposed ? GitCredentialsType.Normal : GitCredentialsType.Tfs;
					try {
						await func (credType);
						GitCredentials.StoreCredentials (credType);
						retry = false;
					} catch (AuthenticationException e) {
						GitCredentials.InvalidateCredentials (credType);
						retry = await Runtime.RunInMainThread (() => HandleAuthenticationException (e));
						if (!retry)
							monitor?.ReportError (e.Message, null);
					} catch (VersionControlException e) {
						GitCredentials.InvalidateCredentials (credType);
						monitor?.ReportError (e.Message, null);
						retry = false;
					} catch (UserCancelledException e) {
						GitCredentials.StoreCredentials (credType);
						retry = false;
						throw new VersionControlException (e.Message, e);
					} catch (LibGit2SharpException e) {
						GitCredentials.InvalidateCredentials (credType);

						if (e.Message == GettextCatalog.GetString (GitCredentials.UserCancelledExceptionMessage))
							throw new VersionControlException (e.Message, e);

						if (credType == GitCredentialsType.Tfs) {
							retry = true;
							tfsSession.Dispose ();
							onRetry?.Invoke ();
							continue;
						}

						string message;
						// TODO: Remove me once https://github.com/libgit2/libgit2/pull/3137 goes in.
						if (string.Equals (e.Message, "early EOF", StringComparison.OrdinalIgnoreCase))
							message = GettextCatalog.GetString ("Unable to authorize credentials for the repository.");
						else if (e.Message.StartsWith ("Invalid Content-Type", StringComparison.OrdinalIgnoreCase))
							message = GettextCatalog.GetString ("Not a valid git repository.");
						else if (string.Equals (e.Message, "Received unexpected content-type", StringComparison.OrdinalIgnoreCase))
							message = GettextCatalog.GetString ("Not a valid git repository.");
						else
							message = e.Message;

						throw new VersionControlException (message, e);
					}
				} while (retry);
			}
		}

		public void Fetch (ProgressMonitor monitor, string remote)
		{
			monitor.BeginTask (GettextCatalog.GetString ("Fetching"), 1);
			monitor.Log.WriteLine (GettextCatalog.GetString ("Fetching from '{0}'", remote));
			int progress = 0;

			RunOperation (() => {
				var refSpec = RootRepository.Network.Remotes [remote]?.FetchRefSpecs.Select (spec => spec.Specification);
				RetryUntilSuccess (monitor, credType => LibGit2Sharp.Commands.Fetch (RootRepository, remote, refSpec, new FetchOptions {
					CredentialsProvider = (url, userFromUrl, types) => GitCredentials.TryGet (url, userFromUrl, types, credType),
					OnTransferProgress = tp => OnTransferProgress (tp, monitor, ref progress),
				}, string.Empty));
			}, true);
			monitor.Step (1);
			monitor.EndTask ();
		}

		async Task<(bool, int, GitUpdateOptions)> CommonPreMergeRebase (GitUpdateOptions options, ProgressMonitor monitor, int stashIndex, string branch, string actionButtonTitle, bool isUpdate)
		{
			if (!WaitAndFreezeEvents (monitor.CancellationToken))
				return (false, -1, options);
			monitor.Step (1);

			if ((options & GitUpdateOptions.SaveLocalChanges) != GitUpdateOptions.SaveLocalChanges) {
				const VersionStatus unclean = VersionStatus.Modified | VersionStatus.ScheduledAdd | VersionStatus.ScheduledDelete;
				bool modified = false;
				if ((await GetDirectoryVersionInfoAsync (RootPath, false, true, monitor.CancellationToken)).Any (v => (v.Status & unclean) != VersionStatus.Unversioned))
					modified = true;

				if (modified) {
					if (!PromptToStash (
						GettextCatalog.GetString ("There are local changes that conflict with changes committed in the <b>{0}</b> branch. Would you like to stash the changes and continue?", branch),
						actionButtonTitle,
						isUpdate ? GettextCatalog.GetString ("Automatically stash/unstash changes when merging/rebasing") : null,
						isUpdate ? GitService.StashUnstashWhenUpdating : null))
						return (false, -1, options);

					options |= GitUpdateOptions.SaveLocalChanges;
				}
			}
			if ((options & GitUpdateOptions.SaveLocalChanges) == GitUpdateOptions.SaveLocalChanges) {
				monitor.Log.WriteLine (GettextCatalog.GetString ("Saving local changes"));
				Stash stash;
				if (!TryCreateStash (monitor, GetStashName ("_tmp_"), out stash))
					return (false, - 1, options);

				if (stash != null)
					stashIndex = 0;
				monitor.Step (1);
			}
			return (true, stashIndex, options);
		}

		bool PromptToStash (string messageText, string actionButtonTitle, string dontAskLabel = null, ConfigurationProperty<bool> dontAskProperty = null)
		{
			bool showDontAsk = !string.IsNullOrEmpty (dontAskLabel) && dontAskProperty != null;
			var message = new GenericMessage {
				Text = GettextCatalog.GetString ("Conflicting local changes found"),
				SecondaryText = messageText,
				Icon = Ide.Gui.Stock.Question
			};
			if (showDontAsk) {
				message.AddOption (nameof (dontAskLabel), dontAskLabel, dontAskProperty.Value);
			}
			message.Buttons.Add (AlertButton.Cancel);
			message.Buttons.Add (new AlertButton (actionButtonTitle));
			message.DefaultButton = 1;

			var result = MessageService.GenericAlert (message) != AlertButton.Cancel;
			if (result && showDontAsk)
				dontAskProperty.Value = message.GetOptionValue (nameof (dontAskLabel));
			return result;
		}

		bool ConflictResolver(LibGit2Sharp.Repository repository, ProgressMonitor monitor, Commit resetToIfFail, string message)
		{
			foreach (var conflictFile in repository.Index.Conflicts) {
				ConflictResult res = ResolveConflict (repository.FromGitPath (conflictFile.Ancestor.Path));
				if (res == ConflictResult.Abort) {
					repository.Reset (ResetMode.Hard, resetToIfFail);
					return false;
				}
				if (res == ConflictResult.Skip) {
					RevertAsync (repository.FromGitPath (conflictFile.Ancestor.Path), false, monitor);
					break;
				}
				if (res == Git.ConflictResult.Continue) {
					Add (repository.FromGitPath (conflictFile.Ancestor.Path), false, monitor);
				}
			}
			if (!string.IsNullOrEmpty (message)) {
				var sig = GetSignature ();
				repository.Commit (message, sig, sig);
			}
			return true;
		}

		void CommonPostMergeRebase(int stashIndex, GitUpdateOptions options, ProgressMonitor monitor, Commit oldHead)
		{
			try {
				if ((options & GitUpdateOptions.SaveLocalChanges) == GitUpdateOptions.SaveLocalChanges) {
					monitor.Step (1);

					// Restore local changes
					if (stashIndex != -1) {
						monitor.Log.WriteLine (GettextCatalog.GetString ("Restoring local changes"));
						ApplyStash (monitor, stashIndex);
						// FIXME: No StashApplyStatus.Conflicts here.
						if (RootRepository.Index.Conflicts.Any () && !ConflictResolver (RootRepository, monitor, oldHead, string.Empty))
							PopStash (monitor, stashIndex);
						else
							RunBlockingOperation (() => RootRepository.Stashes.Remove (stashIndex), cancellationToken: monitor.CancellationToken);
						monitor.Step (1);
					}
				}
			} finally {
				ThawEvents ();
				monitor.EndTask ();
			}
		}

		public Task RebaseAsync (string branch, GitUpdateOptions options, ProgressMonitor monitor)
		{
			return RebaseAsync (branch, options, monitor, false);
		}

		async Task RebaseAsync (string branch, GitUpdateOptions options, ProgressMonitor monitor, bool isUpdate)
		{
			int stashIndex = -1;
			var oldHead = RootRepository.Head.Tip;

			try {
				monitor.BeginTask (GettextCatalog.GetString ("Rebasing"), 5);
				var (success, newStashIndex, newOptions) = await CommonPreMergeRebase (options, monitor, stashIndex, branch, GettextCatalog.GetString ("Stash and Rebase"), isUpdate);
				if (!success)
					return;
				stashIndex = newStashIndex;
				options = newOptions;

				RunBlockingOperation (() => {

					// Do a rebase.
					var divergence = RootRepository.ObjectDatabase.CalculateHistoryDivergence (RootRepository.Head.Tip, RootRepository.Branches [branch].Tip);
					var toApply = RootRepository.Commits.QueryBy (new CommitFilter {
						IncludeReachableFrom = RootRepository.Head.Tip,
						ExcludeReachableFrom = divergence.CommonAncestor,
						SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse
					}).ToArray ();

					RootRepository.Reset (ResetMode.Hard, divergence.Another);

					int count = toApply.Length;
					int i = 1;
					foreach (var com in toApply) {
						monitor.Log.WriteLine (GettextCatalog.GetString ("Cherry-picking {0} - {1}/{2}", com.Id, i, count));
						CherryPickResult cherryRes = RootRepository.CherryPick (com, com.Author, new CherryPickOptions {
							CheckoutNotifyFlags = refreshFlags,
							OnCheckoutNotify = (string path, CheckoutNotifyFlags flags) => RefreshFile (path, flags),
						});
						if (cherryRes.Status == CherryPickStatus.Conflicts)
							ConflictResolver (RootRepository, monitor, toApply.Last (), RootRepository.Info.Message ?? com.Message);
						++i;
					}
				}, true, cancellationToken: monitor.CancellationToken);
			} finally {
				CommonPostMergeRebase (stashIndex, options, monitor, oldHead);
			}
		}

		public Task MergeAsync (string branch, GitUpdateOptions options, ProgressMonitor monitor, FastForwardStrategy strategy = FastForwardStrategy.Default)
		{
			return MergeAsync (branch, options, monitor, false, strategy);
		}

		async Task MergeAsync (string branch, GitUpdateOptions options, ProgressMonitor monitor, bool isUpdate, FastForwardStrategy strategy = FastForwardStrategy.Default)
		{
			int stashIndex = -1;

			Signature sig = GetSignature ();
			if (sig == null)
				return;

			var oldHead = RootRepository.Head.Tip;

			try {
				monitor.BeginTask (GettextCatalog.GetString ("Merging"), 5);
				var (success, newStashIndex, newOptions) = await CommonPreMergeRebase (options, monitor, stashIndex, branch, GettextCatalog.GetString ("Stash and Merge"), isUpdate);
				if (!success)
					return;
				stashIndex = newStashIndex;
				options = newOptions;

				// Do a merge.
				MergeResult mergeResult = RunBlockingOperation (() =>
					RootRepository.Merge (branch, sig, new MergeOptions {
						CheckoutNotifyFlags = refreshFlags,
						OnCheckoutNotify = (string path, CheckoutNotifyFlags flags) => RefreshFile (path, flags),
					}), true, monitor.CancellationToken);
				if (mergeResult.Status == MergeStatus.Conflicts)
						ConflictResolver (RootRepository, monitor, RootRepository.Head.Tip, RootRepository.Info.Message);
			} finally {
				CommonPostMergeRebase (stashIndex, GitUpdateOptions.SaveLocalChanges, monitor, oldHead);
			}
		}

		static ConflictResult ResolveConflict (string file)
		{
			ConflictResult res = ConflictResult.Abort;
			Runtime.RunInMainThread (delegate {
				var dlg = new ConflictResolutionDialog ();
				try {
					dlg.Load (file);
					var dres = (Gtk.ResponseType) MessageService.RunCustomDialog (dlg);
					dlg.Hide ();
					switch (dres) {
					case Gtk.ResponseType.Cancel:
						res = ConflictResult.Abort;
						break;
					case Gtk.ResponseType.Close:
						res = ConflictResult.Skip;
						break;
					case Gtk.ResponseType.Ok:
						res = ConflictResult.Continue;
						dlg.Save (file);
						break;
					}
				} finally {
					dlg.Destroy ();
					dlg.Dispose ();
				}
			}).Wait ();
			return res;
		}

		protected override async Task OnCommitAsync (ChangeSet changeSet, ProgressMonitor monitor)
		{
			string message = changeSet.GlobalComment;
			if (string.IsNullOrEmpty (message))
				throw new ArgumentException ("Commit message must not be null or empty!", "message");

			Signature sig = GetSignature ();
			if (sig == null)
				return;

			var repo = (GitRepository)changeSet.Repository;
			var addedFiles = await GetAddedLocalPathItems (changeSet, monitor.CancellationToken);

			RunBlockingOperation (() => {
				try {
					// Unstage added files not included in the changeSet
					if (addedFiles.Any ())
						LibGit2Sharp.Commands.Unstage (RootRepository, addedFiles.ToPathStrings ());
				} catch (Exception ex) {
					LoggingService.LogInternalError ("Failed to commit.", ex);
					return;
				}
				try {
					// Commit
					LibGit2Sharp.Commands.Stage (RootRepository, changeSet.Items.Select (i => i.LocalPath).ToPathStrings ());

					if (changeSet.ExtendedProperties.Contains ("Git.AuthorName"))
						RootRepository.Commit (message, new Signature (
							(string)changeSet.ExtendedProperties ["Git.AuthorName"],
							(string)changeSet.ExtendedProperties ["Git.AuthorEmail"],
							DateTimeOffset.Now), sig);
					else
						RootRepository.Commit (message, sig, sig);
				} catch (Exception ex) {
					LoggingService.LogInternalError ("Failed to commit.", ex);
				} finally {
					// Always at the end, stage again the unstage added files not included in the changeSet
					if (addedFiles.Any ())
						LibGit2Sharp.Commands.Stage (RootRepository, addedFiles.ToPathStrings ());
				}
			}, cancellationToken: monitor.CancellationToken);
		}

		async Task<HashSet<FilePath>> GetAddedLocalPathItems (ChangeSet changeSet, CancellationToken cancellationToken)
		{
			var addedLocalPathItems = new HashSet<FilePath> ();
			try {
				var directoryVersionInfo = await GetDirectoryVersionInfoAsync (changeSet.BaseLocalPath, false, true, cancellationToken);
				const VersionStatus addedStatus = VersionStatus.Versioned | VersionStatus.ScheduledAdd;
				var directoryVersionInfoItems = directoryVersionInfo.Where (vi => vi.Status == addedStatus);

				foreach (var item in directoryVersionInfoItems)
					foreach (var changeSetItem in changeSet.Items)
						if (item.LocalPath != changeSetItem.LocalPath)
							addedLocalPathItems.Add (item.LocalPath);
			} catch (Exception ex) {
				LoggingService.LogInternalError ("Could not get added VersionInfo items.", ex);
			}
			return addedLocalPathItems;
		}

		public bool IsUserInfoDefault ()
		{
			string name = null;
			string email = null;
			try {
				RunOperation (() => {
					name = RootRepository.Config.Get<string> ("user.name").Value;
					email = RootRepository.Config.Get<string> ("user.email").Value;
				});
			} catch {
				name = email = null;
			}
			return name == null && email == null;
		}

		public void GetUserInfo (out string name, out string email, Components.Window parent = null)
		{
			try {
				string lname = null, lemail = null;
				RunOperation (() => {
					lname = RootRepository.Config.Get<string> ("user.name").Value;
					lemail = RootRepository.Config.Get<string> ("user.email").Value;
				});
				name = lname;
				email = lemail;
			} catch {
				string dlgName = null, dlgEmail = null;

				Runtime.RunInMainThread (() => {
					var dlg = new UserGitConfigDialog ();
					try {
						if ((Gtk.ResponseType)MessageService.RunCustomDialog (dlg, parent) == Gtk.ResponseType.Ok) {
							dlgName = dlg.UserText;
							dlgEmail = dlg.EmailText;
							SetUserInfo (dlgName, dlgEmail);
						}
					} finally {
						dlg.Destroy ();
						dlg.Dispose ();
					}
				}).Wait ();

				name = dlgName;
				email = dlgEmail;
			}
		}

		public void SetUserInfo (string name, string email)
		{
			RunOperation (() => {
				RootRepository.Config.Set ("user.name", name);
				RootRepository.Config.Set ("user.email", email);
			});
		}

		protected override Task OnCheckoutAsync (FilePath targetLocalPath, Revision rev, bool recurse, ProgressMonitor monitor)
		{
			int transferProgress = 0;
			int checkoutProgress = 0;

			try {
				monitor.BeginTask (GettextCatalog.GetString ("Cloning…"), 2);
				bool skipSubmodules = false;
				RunOperation (() => RetryUntilSuccess (monitor, credType => {
					var options = new CloneOptions {
						CredentialsProvider = (url, userFromUrl, types) => {
							transferProgress = checkoutProgress = 0;
							return GitCredentials.TryGet (url, userFromUrl, types, credType);
						},
						RepositoryOperationStarting = ctx => {
							Runtime.RunInMainThread (() => {
								monitor.Log.WriteLine (GettextCatalog.GetString ("Checking out repository at '{0}'"), ctx.RepositoryPath);
							});
							return true;
						},
						OnTransferProgress = (tp) => OnTransferProgress (tp, monitor, ref transferProgress),
						OnCheckoutProgress = (path, completedSteps, totalSteps) => {
							OnCheckoutProgress (completedSteps, totalSteps, monitor, ref checkoutProgress);
							Runtime.RunInMainThread (() => {
								monitor.Log.WriteLine (GettextCatalog.GetString ("Checking out file '{0}'"), path);
							});
						}
					};

					try {
						RootPath = LibGit2Sharp.Repository.Clone (Url, targetLocalPath, options);
					} catch (UserCancelledException) {
						return;
					}
					var updateOptions = new SubmoduleUpdateOptions {
						Init = true,
						CredentialsProvider = options.CredentialsProvider,
						OnTransferProgress = options.OnTransferProgress,
						OnCheckoutProgress = options.OnCheckoutProgress,
					};
					monitor.Step (1);
					try {
						if (!skipSubmodules)
							RecursivelyCloneSubmodules (RootPath, updateOptions, monitor);
					} catch (Exception e) {
						LoggingService.LogError ("Cloning submodules failed", e);
						FileService.DeleteDirectory (RootPath);
						skipSubmodules = true;
						throw e;
					}
				}), true);

				if (monitor.CancellationToken.IsCancellationRequested || RootPath.IsNull)
					return Task.CompletedTask;

				RootPath = RootPath.CanonicalPath.ParentDirectory;

				RootRepository = new LibGit2Sharp.Repository (RootPath);
				InitFileWatcher ();
				if (skipSubmodules) {
					MessageService.ShowError (GettextCatalog.GetString("Cloning submodules failed"), GettextCatalog.GetString ("Please use the command line client to init the submodules manually."));
				}
				return Task.CompletedTask;
			} catch (Exception e) {
				LoggingService.LogInternalError ("Error while cloning repository " + rev + " recuse: " + recurse, e);
				throw e;
			} finally {
				monitor.EndTask ();
			}
		}

		static void RecursivelyCloneSubmodules (string repoPath, SubmoduleUpdateOptions updateOptions, ProgressMonitor monitor)
		{
			var submodules = new List<string> ();
			using (var repo = new LibGit2Sharp.Repository (repoPath)) {
				// Iterate through the submodules (where the submodule is in the index),
				// and clone them.
				var submoduleArray = repo.Submodules.Where (sm => sm.RetrieveStatus ().HasFlag (SubmoduleStatus.InIndex)).ToArray ();
				monitor.BeginTask (GettextCatalog.GetString ("Cloning submodules…"), submoduleArray.Length);
				try {
					foreach (var sm in submoduleArray) {
						if (monitor.CancellationToken.IsCancellationRequested) {
							throw new UserCancelledException ("Recursive clone of submodules was cancelled.");
						}

						Runtime.RunInMainThread (() => {
							monitor.Log.WriteLine (GettextCatalog.GetString ("Checking out submodule at '{0}'…", sm.Path));
							monitor.Step (1);
						});
						repo.Submodules.Update (sm.Name, updateOptions);

						submodules.Add (Path.Combine (repo.Info.WorkingDirectory, sm.Path));
					}
				} finally {
					monitor.EndTask ();
				}
			}

			// If we are continuing the recursive operation, then
			// recurse into nested submodules.
			// Check submodules to see if they have their own submodules.
			foreach (string path in submodules) {
				RecursivelyCloneSubmodules (path, updateOptions, monitor);
			}
		}

		protected override async Task OnRevertAsync (FilePath [] localPaths, bool recurse, ProgressMonitor monitor)
		{
			foreach (var group in GroupByRepositoryRoot (localPaths)) {
				var toCheckout = new HashSet<FilePath> ();
				var toUnstage = new HashSet<FilePath> ();

				foreach (var item in group)
					if (item.IsDirectory) {
						foreach (var vi in await GetDirectoryVersionInfoAsync (item, false, recurse, monitor.CancellationToken))
							if (!vi.IsDirectory) {
								if (vi.Status == VersionStatus.Unversioned)
									continue;

								if ((vi.Status & VersionStatus.ScheduledAdd) == VersionStatus.ScheduledAdd)
									toUnstage.Add (vi.LocalPath);
								else
									toCheckout.Add (vi.LocalPath);
							}
					} else {
						if (!TryGetVersionInfo (item, out var vi))
							continue;
						if (vi.Status == VersionStatus.Unversioned)
							continue;

						if ((vi.Status & VersionStatus.ScheduledAdd) == VersionStatus.ScheduledAdd)
							toUnstage.Add (vi.LocalPath);
						else
							toCheckout.Add (vi.LocalPath);
					}

				monitor.BeginTask (GettextCatalog.GetString ("Reverting files"), 1);

				RunBlockingOperation (group.Key, repository => {
					var repoFiles = repository.ToGitPath (toCheckout);
					int progress = 0;
					if (toCheckout.Any ()) {
						repository.CheckoutPaths ("HEAD", repoFiles, new CheckoutOptions {
							OnCheckoutProgress = (path, completedSteps, totalSteps) => OnCheckoutProgress (completedSteps, totalSteps, monitor, ref progress),
							CheckoutModifiers = CheckoutModifiers.Force,
							CheckoutNotifyFlags = refreshFlags,
							OnCheckoutNotify = delegate (string path, CheckoutNotifyFlags notifyFlags) {
								if ((notifyFlags & CheckoutNotifyFlags.Untracked) == 0)
									return RefreshFile (repository, path, notifyFlags);
								return true;
							}
						});
						LibGit2Sharp.Commands.Stage (repository, repoFiles);
					}

					if (toUnstage.Any ())
						LibGit2Sharp.Commands.Unstage (repository, repository.ToGitPath (toUnstage).ToArray ());
				}, true, monitor.CancellationToken);
				monitor.EndTask ();
			}
		}

		protected override Task OnRevertRevisionAsync (FilePath localPath, Revision revision, ProgressMonitor monitor)
		{
			throw new NotSupportedException ();
		}

		protected override Task OnRevertToRevisionAsync (FilePath localPath, Revision revision, ProgressMonitor monitor)
		{
			throw new NotSupportedException ();
		}

		protected override Task OnAddAsync (FilePath[] localPaths, bool recurse, ProgressMonitor monitor)
		{
			foreach (var group in GroupByRepository (localPaths)) {
				var files = group.Where (f => !f.IsDirectory);
				if (files.Any ())
					RunBlockingOperation (() => LibGit2Sharp.Commands.Stage (group.Key, group.Key.ToGitPath (files)), cancellationToken: monitor.CancellationToken);
			}
			return Task.CompletedTask;
		}

		protected override async Task OnDeleteFilesAsync (FilePath[] localPaths, bool force, ProgressMonitor monitor, bool keepLocal)
		{
			DeleteCore (localPaths, keepLocal, monitor);

			foreach (var path in localPaths) {
				if (keepLocal) {
					// Undo addition of files.
					VersionInfo info = await GetVersionInfoAsync (path, VersionInfoQueryFlags.IgnoreCache, monitor.CancellationToken);
					if (info != null && info.HasLocalChange (VersionStatus.ScheduledAdd)) {
						// Revert addition.
						RevertAsync (path, false, monitor);
					}
				} else {
					// Untracked files are not deleted by the rm command, so delete them now
					if (File.Exists (path))
						File.Delete (path);
				}
			}
		}

		protected override async Task OnDeleteDirectoriesAsync (FilePath[] localPaths, bool force, ProgressMonitor monitor, bool keepLocal)
		{
			DeleteCore (localPaths, keepLocal, monitor);

			foreach (var path in localPaths) {
				if (keepLocal) {
					// Undo addition of directories and files.
					foreach (var info in await GetDirectoryVersionInfoAsync (path, false, true, monitor.CancellationToken)) {
						if (info != null && info.HasLocalChange (VersionStatus.ScheduledAdd)) {
							// Revert addition.
							await RevertAsync (path, true, monitor);
						}
					}
				}
			}

			if (!keepLocal) {
				// Untracked files are not deleted by the rm command, so delete them now
				foreach (var f in localPaths) {
					if (Directory.Exists (f)) {
						FileService.AssertCanDeleteDirectory (f, this.RootPath);
						Directory.Delete (f, true);
					}
				}
			}
		}

		void DeleteCore (FilePath[] localPaths, bool keepLocal, ProgressMonitor monitor)
		{
			foreach (var group in GroupByRepository (localPaths)) {
				if (!keepLocal)
					foreach (var f in localPaths) {
						if (File.Exists (f))
							File.Delete (f);
						else if (Directory.Exists (f)) {
							FileService.AssertCanDeleteDirectory (f, RootPath);
							Directory.Delete (f, true);
						}
					}

				RunBlockingOperation (() => {
					var files = group.Key.ToGitPath (group);
					LibGit2Sharp.Commands.Remove (group.Key, files, !keepLocal, null);
				}, cancellationToken: monitor.CancellationToken);
			}
		}

		protected override Task<string> OnGetTextAtRevisionAsync (FilePath repositoryPath, Revision revision, CancellationToken cancellationToken)
		{
			var gitRev = (GitRevision)revision;
			return RunOperationAsync (repositoryPath, repository => GetCommitTextContent (gitRev.GetCommit (repository), repositoryPath, repository));
		}




		public override Task<DiffInfo> GenerateDiffAsync (FilePath baseLocalPath, VersionInfo versionInfo)
		{
			return RunOperationAsync (versionInfo.LocalPath, repository => {
				try {
					var patch = repository.Diff.Compare<Patch> (repository.Head?.Tip?.Tree, DiffTargets.WorkingDirectory | DiffTargets.Index, new [] { repository.ToGitPath (versionInfo.LocalPath) });
					// Trim the header by taking out the first 2 lines.
					int diffStart = patch.Content.IndexOf ('\n', patch.Content.IndexOf ('\n') + 1);
					return new DiffInfo (baseLocalPath, versionInfo.LocalPath, patch.Content.Substring (diffStart + 1));
				} catch (Exception ex) {
					LoggingService.LogError ("Could not get diff for file '" + versionInfo.LocalPath + "'", ex);
					return null;
				}
			});
		}

		public override async Task<DiffInfo []> PathDiffAsync (FilePath baseLocalPath, FilePath [] localPaths, bool remoteDiff, CancellationToken cancellationToken)
		{
			var diffs = new List<DiffInfo> ();
			VersionInfo[] vinfos = await GetDirectoryVersionInfoAsync (baseLocalPath, localPaths, false, true, cancellationToken);
			foreach (VersionInfo vi in vinfos) {
				var diff = await GenerateDiffAsync (baseLocalPath, vi);
				if (diff != null)
					diffs.Add (diff);
			}
			return diffs.ToArray ();
		}

		Blob GetBlob (Commit c, FilePath file, LibGit2Sharp.Repository repo)
		{
			TreeEntry entry = c [repo.ToGitPath (file)];
			return entry != null ? (Blob)entry.Target : null;
		}

		string GetCommitTextContent (Commit c, FilePath file, LibGit2Sharp.Repository repo)
		{
			Blob blob = GetBlob (c, file, repo);
			if (blob == null)
				return string.Empty;

			return blob.IsBinary ? String.Empty : blob.GetContentText ();
		}

		public async Task<string> GetCurrentRemoteAsync (CancellationToken cancellationToken = default)
		{
			var headRemote = RunSafeOperation (() => RootRepository.Head?.RemoteName);
			if (!string.IsNullOrEmpty (headRemote))
				return headRemote;

			var remotes = new List<string> ((await GetRemotesAsync (cancellationToken)).Select (r => r.Name));
			if (remotes.Count == 0)
				return null;

			return remotes.Contains ("origin") ? "origin" : remotes [0];
		}

		public void Push (ProgressMonitor monitor, string remote, string remoteBranch)
		{
			bool success = true;

			RunBlockingOperation (() => {
				var branch = RootRepository.Head;
				if (branch.TrackedBranch == null) {
					RootRepository.Branches.Update (branch, b => b.TrackedBranch = "refs/remotes/" + remote + "/" + remoteBranch);
				}
			}, true, monitor.CancellationToken);

			RunOperation (() => {
				RetryUntilSuccess (monitor, credType =>
					RootRepository.Network.Push (RootRepository.Network.Remotes [remote], "refs/heads/" + remoteBranch, new PushOptions {
						OnPushStatusError = pushStatusErrors => success = false,
						CredentialsProvider = (url, userFromUrl, types) => GitCredentials.TryGet (url, userFromUrl, types, credType)
					})
				);
			}, true);

			if (!success)
				return;

			monitor.ReportSuccess (GettextCatalog.GetString ("Push operation successfully completed."));
		}

		public void CreateBranchFromCommit (string name, Commit id)
		{
			RunBlockingOperation (() => RootRepository.CreateBranch (name, id));
		}

		public void CreateBranch (string name, string trackSource, string targetRef)
		{
			RunBlockingOperation (() => CreateBranch (name, trackSource, targetRef, RootRepository));
		}

		void CreateBranch (string name, string trackSource, string targetRef, LibGit2Sharp.Repository repo)
		{
			Commit c = null;
			if (!string.IsNullOrEmpty (trackSource))
				c = repo.Lookup<Commit> (trackSource);

			repo.Branches.Update (
				repo.CreateBranch (name, c ?? repo.Head.Tip),
				bu => bu.TrackedBranch = targetRef);
		}

		public void SetBranchTrackRef (string name, string trackSource, string trackRef)
		{
			RunBlockingOperation (() => {
				var branch = RootRepository.Branches [name];
				if (branch != null) {
					RootRepository.Branches.Update (branch, bu => bu.TrackedBranch = trackRef);
				} else
					CreateBranch (name, trackSource, trackRef, RootRepository);
			});
		}

		public void RemoveBranch (string name)
		{
			RunBlockingOperation (() => RootRepository.Branches.Remove (name));
		}

		public void RenameBranch (string name, string newName)
		{
			RunBlockingOperation (() => RootRepository.Branches.Rename (name, newName, true));
		}

		public Task<IEnumerable<Remote>> GetRemotesAsync (CancellationToken cancellationToken = default)
		{
			// TODO: access to Remote props is not under our control
			return RunOperationAsync (() => RootRepository.Network.Remotes.Cast<Remote> (), cancellationToken:cancellationToken);
		}

		public bool IsBranchMerged (string branchName)
		{
			// check if a branch is merged into HEAD
			return RunOperation (() => {
				var tip = RootRepository.Branches [branchName].Tip.Sha;
				return RootRepository.Commits.Any (c => c.Sha == tip);
			});
		}

		public void RenameRemote (string name, string newName)
		{
			RunBlockingOperation (() => RootRepository.Network.Remotes.Rename (name, newName));
		}

		public void ChangeRemoteUrl (string name, string url)
		{
			RunBlockingOperation (() =>
				RootRepository.Network.Remotes.Update (
					name,
					r => r.Url = url
				));
		}

		public void ChangeRemotePushUrl (string name, string url)
		{
			RunBlockingOperation (() =>
				RootRepository.Network.Remotes.Update (
					name,
					r => r.PushUrl = url
				));
		}

		public void AddRemote (string name, string url, bool importTags)
		{
			if (string.IsNullOrEmpty (name))
				throw new InvalidOperationException ("Name not set");

			RunBlockingOperation (() =>
				RootRepository.Network.Remotes.Update (RootRepository.Network.Remotes.Add (name, url).Name,
					r => r.TagFetchMode = importTags ? TagFetchMode.All : TagFetchMode.Auto));
		}

		public void RemoveRemote (string name)
		{
			RunBlockingOperation (() => RootRepository.Network.Remotes.Remove (name));
		}

		public IEnumerable<Branch> GetBranches ()
		{
			// TODO: access to Remote props is not under our control
			return RunOperation (() => RootRepository.Branches.Where (b => !b.IsRemote));
		}

		public IEnumerable<string> GetTags ()
		{
			return RunOperation (() => RootRepository.Tags.Select (t => t.FriendlyName)).ToArray ();
		}

		public void AddTag (string name, Revision rev, string message)
		{
			Signature sig = GetSignature ();
			if (sig == null)
				return;

			var gitRev = (GitRevision)rev;
			RunBlockingOperation (() => RootRepository.Tags.Add (name, gitRev.GetCommit (RootRepository), sig, message));
		}

		public void RemoveTag (string name)
		{
			RunBlockingOperation (() => RootRepository.Tags.Remove (name));
		}

		public Task PushTagAsync (string name)
		{
			return RunOperation (async () => {
				await RetryUntilSuccessAsync (null, async credType => RootRepository.Network.Push (RootRepository.Network.Remotes [await GetCurrentRemoteAsync ()], "refs/tags/" + name + ":refs/tags/" + name, new PushOptions {
					CredentialsProvider = (url, userFromUrl, types) => GitCredentials.TryGet (url, userFromUrl, types, credType),
				}));
			}, true);
		}

		public IEnumerable<string> GetRemoteBranches (string remoteName)
		{
			return RunOperation (() => RootRepository.Branches
				.Where (b => b.IsRemote && b.RemoteName == remoteName)
				.Select (b => b.FriendlyName.Substring (b.FriendlyName.IndexOf ('/') + 1)))
				.ToArray ();
		}

		public string GetCurrentBranch ()
		{
			return RunSafeOperation (() => RootRepository.Head.FriendlyName);
		}

		async Task SwitchBranchInternalAsync (ProgressMonitor monitor, string branch)
		{
			int progress = 0;
			await ExclusiveOperationFactory.StartNew (() => LibGit2Sharp.Commands.Checkout (RootRepository, branch, new CheckoutOptions {
				OnCheckoutProgress = (path, completedSteps, totalSteps) => OnCheckoutProgress (completedSteps, totalSteps, monitor, ref progress),
				OnCheckoutNotify = (string path, CheckoutNotifyFlags flags) => RefreshFile (path, flags),
				CheckoutNotifyFlags = refreshFlags,
			}), monitor.CancellationToken);

			if (GitService.StashUnstashWhenSwitchingBranches) {
				try {
					// Restore the branch stash
					var stashIndex = RunOperation (() => GetStashForBranch (RootRepository.Stashes, branch));
					if (stashIndex != -1)
						PopStash (monitor, stashIndex);
				} catch (Exception e) {
					monitor.ReportError (GettextCatalog.GetString ("Restoring stash for branch {0} failed", branch), e);
				}
			}

			monitor.Step (1);
			await Runtime.RunInMainThread (() => {
				BranchSelectionChanged?.Invoke (this, EventArgs.Empty);
			});
		}

		public async Task<bool> SwitchToBranchAsync (ProgressMonitor monitor, string branch)
		{
			Signature sig = GetSignature ();
			Stash stash;
			int stashIndex = -1;
			if (sig == null)
				return false;

			if (!WaitAndFreezeEvents (monitor.CancellationToken))
				return false;
			try {
				// try to switch without stashing
				monitor.BeginTask (GettextCatalog.GetString ("Switching to branch {0}", branch), 2);
				await SwitchBranchInternalAsync (monitor, branch);
				return true;
			} catch (CheckoutConflictException ex) {
				// retry with stashing
				monitor.EndTask ();
				if (!GitService.StashUnstashWhenSwitchingBranches) {
					if (!PromptToStash (
						GettextCatalog.GetString ("There are local changes that conflict with changes committed in the <b>{0}</b> branch. Would you like to stash the changes and continue with the checkout?", branch),
						GettextCatalog.GetString ("Stash and Switch"),
						GettextCatalog.GetString ("Automatically stash/unstash changes when switching branches"),
						GitService.StashUnstashWhenSwitchingBranches)) {
						// if canceled, report the error and return
						monitor.ReportError (GettextCatalog.GetString ("Switching to branch {0} failed", branch), ex);
						return false;
					}
				}

				// stash automatically is selected or user requested a stash

				monitor.BeginTask (GettextCatalog.GetString ("Switching to branch {0}", branch), 4);
				// Remove the stash for this branch, if exists
				// TODO: why do with do this?
				string currentBranch = RootRepository.Head.FriendlyName;
				stashIndex = RunOperation (() => GetStashForBranch (RootRepository.Stashes, currentBranch));
				if (stashIndex != -1)
					RunBlockingOperation (() => RootRepository.Stashes.Remove (stashIndex), cancellationToken: monitor.CancellationToken);

				if (!TryCreateStash (monitor, GetStashName (currentBranch), out stash))
					return false;

				monitor.Step (1);

				try {
					await SwitchBranchInternalAsync (monitor, branch);
					return true;
				} catch (Exception e) {
					monitor.ReportError (GettextCatalog.GetString ("Switching to branch {0} failed", branch), e);
				}
			} catch (Exception ex) {
				monitor.ReportError (GettextCatalog.GetString ("Switching to branch {0} failed", branch), ex);
			} finally {
				monitor.EndTask ();
				ThawEvents ();
			}
			return false;
		}

		static string GetStashName (string branchName)
		{
			return "__MD_" + branchName;
		}

		public static string GetStashBranchName (string stashName)
		{
			return stashName.StartsWith ("__MD_", StringComparison.Ordinal) ? stashName.Substring (5) : null;
		}

		static int GetStashForBranch (StashCollection stashes, string branchName)
		{
			string sn = GetStashName (branchName);
			int count = stashes.Count ();
			for (int i = 0; i < count; ++i) {
				if (stashes[i].Message.IndexOf (sn, StringComparison.InvariantCulture) != -1)
					return i;
			}
			return -1;
		}

		public ChangeSet GetPushChangeSet (string remote, string branch)
		{
			ChangeSet cset = CreateChangeSet (RootPath);

			RunOperation (() => {
				Commit reference = RootRepository.Branches [remote + "/" + branch].Tip;
				Commit compared = RootRepository.Head.Tip;

				foreach (var change in GitUtil.CompareCommits (RootRepository, reference, compared)) {
					VersionStatus status;
					switch (change.Status) {
					case ChangeKind.Added:
					case ChangeKind.Copied:
						status = VersionStatus.ScheduledAdd;
						break;
					case ChangeKind.Deleted:
						status = VersionStatus.ScheduledDelete;
						break;
					case ChangeKind.Renamed:
						status = VersionStatus.ScheduledReplace;
						break;
					default:
						status = VersionStatus.Modified;
						break;
					}
					var vi = new VersionInfo (RootRepository.FromGitPath (change.Path), "", false, status | VersionStatus.Versioned, null, VersionStatus.Versioned, null);
					cset.AddFile (vi);
				}
			});
			return cset;
		}

		public DiffInfo[] GetPushDiff (string remote, string branch)
		{
			return RunOperation (() => {
				Commit reference = RootRepository.Branches [remote + "/" + branch].Tip;
				Commit compared = RootRepository.Head.Tip;

				var diffs = new List<DiffInfo> ();
				var patch = RootRepository.Diff.Compare<Patch> (reference.Tree, compared.Tree);
				foreach (var change in GitUtil.CompareCommits (RootRepository, reference, compared)) {
					string path;
					switch (change.Status) {
					case ChangeKind.Deleted:
					case ChangeKind.Renamed:
						path = change.OldPath;
						break;
					default:
						path = change.Path;
						break;
					}

					// Trim the header by taking out the first 2 lines.
					int diffStart = patch [path].Patch.IndexOf ('\n', patch [path].Patch.IndexOf ('\n') + 1);
					diffs.Add (new DiffInfo (RootPath, RootRepository.FromGitPath (path), patch [path].Patch.Substring (diffStart + 1)));
				}
				return diffs.ToArray ();
			});
		}

		protected override async Task OnMoveFileAsync (FilePath localSrcPath, FilePath localDestPath, bool force, ProgressMonitor monitor)
		{
			VersionInfo vi = await GetVersionInfoAsync (localSrcPath, VersionInfoQueryFlags.IgnoreCache, monitor.CancellationToken);
			if (vi == null || !vi.IsVersioned) {
				await base.OnMoveFileAsync (localSrcPath, localDestPath, force, monitor);
				return;
			}

			var srcRepo = GetRepository (localSrcPath);
			var dstRepo = GetRepository (localDestPath);

			vi = await GetVersionInfoAsync (localDestPath, VersionInfoQueryFlags.IgnoreCache, monitor.CancellationToken);
			RunBlockingOperation (() => {
				if (vi != null && ((vi.Status & (VersionStatus.ScheduledDelete | VersionStatus.ScheduledReplace)) != VersionStatus.Unversioned))
					LibGit2Sharp.Commands.Unstage (dstRepo, localDestPath);

				if (srcRepo == dstRepo) {
					LibGit2Sharp.Commands.Move (srcRepo, localSrcPath, localDestPath);
					ClearCachedVersionInfo (localSrcPath, localDestPath);
				} else {
					File.Copy (localSrcPath, localDestPath);
					LibGit2Sharp.Commands.Remove (srcRepo, localSrcPath, true);
					LibGit2Sharp.Commands.Stage (dstRepo, localDestPath);
				}
			}, cancellationToken: monitor.CancellationToken);
		}

		protected override async Task OnMoveDirectoryAsync (FilePath localSrcPath, FilePath localDestPath, bool force, ProgressMonitor monitor)
		{
			VersionInfo[] versionedFiles = await GetDirectoryVersionInfoAsync (localSrcPath, false, true, monitor.CancellationToken);
			await base.OnMoveDirectoryAsync (localSrcPath, localDestPath, force, monitor);
			monitor.BeginTask (GettextCatalog.GetString ("Moving files"), versionedFiles.Length);
			foreach (VersionInfo vif in versionedFiles) {
				if (vif.IsDirectory)
					continue;
				FilePath newDestPath = vif.LocalPath.ToRelative (localSrcPath).ToAbsolute (localDestPath);
				await AddAsync (newDestPath, false, monitor);
				monitor.Step (1);
			}
			monitor.EndTask ();
		}

		object blameLock = new object ();

		public override Task<Annotation []> GetAnnotationsAsync (FilePath repositoryPath, Revision since, CancellationToken cancellationToken)
		{
			return RunOperation (repositoryPath, async repository => {
				Commit hc = GetHeadCommit (repository);
				Commit sinceCommit = since != null ? ((GitRevision)since).GetCommit (repository) : null;
				if (hc == null)
					return Array.Empty<Annotation> ();

				var list = new List<Annotation> ();

				var gitPath = repository.ToGitPath (repositoryPath);
				var status = repository.RetrieveStatus (gitPath);
				if (status != FileStatus.NewInIndex && status != FileStatus.NewInWorkdir) {
					lock (blameLock) {
						foreach (var hunk in repository.Blame (gitPath, new BlameOptions { FindExactRenames = true, StartingAt = sinceCommit })) {
							var commit = hunk.FinalCommit;
							var author = hunk.FinalSignature;
							var working = new Annotation (new GitRevision (this, gitPath, commit), author.Name, author.When.LocalDateTime, String.Format ("<{0}>", author.Email));
							for (int i = 0; i < hunk.LineCount; ++i)
								list.Add (working);
						}
					}
				}

				if (sinceCommit == null) {
					var baseText = await GetBaseTextAsync (repositoryPath, cancellationToken);
					await Runtime.RunInMainThread (delegate {
						var baseDocument = Mono.TextEditor.TextDocument.CreateImmutableDocument (baseText);
						var workingDocument = Mono.TextEditor.TextDocument.CreateImmutableDocument (TextFileUtility.GetText (repositoryPath));
						var nextRev = new Annotation (null, GettextCatalog.GetString ("<uncommitted>"), DateTime.MinValue, null, GettextCatalog.GetString ("working copy"));
						foreach (var hunk in baseDocument.Diff (workingDocument, includeEol: false)) {
							list.RemoveRange (hunk.RemoveStart - 1, hunk.Removed);
							for (int i = 0; i < hunk.Inserted; ++i) {
								if (hunk.InsertStart + i >= list.Count)
									list.Add (nextRev);
								else
									list.Insert (hunk.InsertStart - 1, nextRev);
							}
						}
					});
				}

				return list.ToArray ();
			});
		}

		protected override Task OnIgnoreAsync (FilePath[] localPath, CancellationToken cancellationToken)
		{
			var ignored = new List<FilePath> ();
			string gitignore = RootPath + Path.DirectorySeparatorChar + ".gitignore";
			string txt;
			if (File.Exists (gitignore)) {
				using (var br = new StreamReader (gitignore)) {
					while ((txt = br.ReadLine ()) != null) {
						ignored.Add (txt);
					}
				}
			}

			var sb = StringBuilderCache.Allocate ();
			RunBlockingOperation (() => {
				foreach (var path in localPath.Except (ignored))
					sb.AppendLine (RootRepository.ToGitPath (path));

				File.AppendAllText (RootPath + Path.DirectorySeparatorChar + ".gitignore", StringBuilderCache.ReturnAndFree (sb));
				LibGit2Sharp.Commands.Stage (RootRepository, ".gitignore");
			});
			return Task.CompletedTask;
		}

		protected override Task OnUnignoreAsync (FilePath[] localPath, CancellationToken cancellationToken)
		{
			var ignored = new List<string> ();
			string gitignore = RootPath + Path.DirectorySeparatorChar + ".gitignore";
			string txt;
			if (File.Exists (gitignore)) {
				using (var br = new StreamReader (RootPath + Path.DirectorySeparatorChar + ".gitignore")) {
					while ((txt = br.ReadLine ()) != null) {
						ignored.Add (txt);
					}
				}
			}

			var sb = new StringBuilder ();
			RunBlockingOperation (() => {
				foreach (var path in ignored.Except (RootRepository.ToGitPath (localPath)))
					sb.AppendLine (path);

				File.WriteAllText (RootPath + Path.DirectorySeparatorChar + ".gitignore", sb.ToString ());
				LibGit2Sharp.Commands.Stage (RootRepository, ".gitignore");
			});
			return Task.CompletedTask;
		}
	}

	public class GitRevision: Revision
	{
		readonly string rev;

		internal FilePath FileForChanges { get; set; }

		public string GitRepository {
			get; private set;
		}

		public GitRevision (Repository repo, string gitRepository, Commit commit) : base(repo)
		{
			GitRepository = gitRepository;
			rev = commit != null ? commit.Id.Sha : "";
		}

		public GitRevision (Repository repo, string gitRepository, Commit commit, DateTime time, string author, string message) : base(repo, time, author, message)
		{
			GitRepository = gitRepository;
			rev = commit != null ? commit.Id.Sha : "";
		}

		public override string ToString ()
		{
			return rev;
		}

		string shortName;
		public override string ShortName {
			get {
				if (shortName != null)
					return shortName;
				return shortName = rev.Length > 10 ? rev.Substring (0, 10) : rev;
			}
		}

		internal Commit GetCommit (LibGit2Sharp.Repository repository)
		{
			if (repository.Info.WorkingDirectory != GitRepository)
				throw new ArgumentException ("Commit does not belog to the repository", nameof (repository));
			return repository.Lookup<Commit> (rev);
		}

		public override Task<Revision> GetPreviousAsync (CancellationToken cancellationToken)
		{
			var repo = (GitRepository)Repository;
			return repo.RunOperationAsync (GitRepository, repository => GetPrevious (repository), cancellationToken: cancellationToken);
		}

		internal Revision GetPrevious (LibGit2Sharp.Repository repository)
		{
			if (repository.Info.WorkingDirectory != GitRepository)
				throw new ArgumentException ("Commit does not belog to the repository", nameof (repository));
			var id = repository.Lookup<Commit> (rev)?.Parents.FirstOrDefault ();
			return id == null ? null : new GitRevision (Repository, GitRepository, id);
		}
	}

	static class TaskFailureExtensions
	{
		public static void RunWaitAndCapture (this Task task)
		{
			try {
				task.Wait ();
			} catch (AggregateException ex) {
				var exception = ex.FlattenAggregate ().InnerException;
				ExceptionDispatchInfo.Capture (exception).Throw ();
			}
		}

		public static T RunWaitAndCapture<T> (this Task<T> task)
		{
			try {
				return task.Result;
			} catch (AggregateException ex) {
				var exception = ex.FlattenAggregate ().InnerException;
				ExceptionDispatchInfo.Capture (exception).Throw ();
				throw;
			}
		}
	}
}
