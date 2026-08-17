Waiter Video Library

Place per-waiter video files (MP4) under this folder to have them discovered by `JackOnTheRocksWaiterLibraryManager`.

Directory layout options:

1) Per-waiter subfolders (recommended)

Assets/StreamingAssets/Waiters/Waitress_Female_1/Female - 2 - 5 Second.mp4
Assets/StreamingAssets/Waiters/Waitress_Female_1/Female 1 - 5 Second.mp4
Assets/StreamingAssets/Waiters/Waiter_Male_1/Male 5 second.mp4

2) Flat folder with filename prefix

Assets/StreamingAssets/Waiters/Waitress_Female_1_Female-2-5-Second.mp4

Usage:
- After placing MP4 files, open the scene with a Canvas and add a `RawImage` to preview video playback.
- Create an empty GameObject and attach `JackOnTheRocksWaiterLibraryManager` and `JackOnTheRocksWaiterVideoPreviewUI` components.
- Assign `waiterDropdown`, `videoListContent` (RectTransform), `refreshButton`, `stopButton`, `previewTarget` and a simple `videoButtonPrefab` (a Button with a Text child).
- Click `Refresh` at runtime to scan `StreamingAssets/Waiters` and populate the UI.

Notes:
- Replace the placeholder .mp4 files with the real binary MP4 files you uploaded.
- WebGL builds: StreamingAssets paths are served differently; the manager attempts to handle WebGL by returning the absolute path.
- If VideoPlayer fails to play local files on your platform, ensure correct permissions and paths.
