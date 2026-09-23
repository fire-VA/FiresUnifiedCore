using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FiresCore.IO
{
    // One Windows change-notification handle (ReadDirectoryChangesW, overlapped) on one folder, read on its own
    // background thread. The OS reports each change as it happens, so nothing is listed or polled. The thread waits on
    // managed events: Mono's shutdown aborts background threads, and a thread parked in a native wait never answers it.
    internal sealed class DirectoryChangeWatch
    {
        private const uint FileListDirectory = 0x0001;
        private const uint FileShareAll = 0x0007;
        private const uint OpenExisting = 3;
        private const uint BackupSemanticsOverlapped = 0x02000000 | 0x40000000;
        private const uint NotifyFileName = 0x001;
        private const uint NotifyDirName = 0x002;
        private const uint NotifySize = 0x008;
        private const uint NotifyLastWrite = 0x010;
        private const uint NotifyCreation = 0x040;
        private const uint NotifyFilter = NotifyFileName | NotifyDirName | NotifySize | NotifyLastWrite | NotifyCreation;
        private const int BufferBytes = 64 * 1024;
        private const int IoCompletedIndex = 0;
        private const int NextEntryOffset = 0;
        private const int ActionOffset = 4;
        private const int NameLengthOffset = 8;
        private const int NameOffset = 12;
        private const int BytesPerChar = 2;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        public readonly string Directory;
        public readonly bool Recursive;

        private readonly Action<DirectoryChangeWatch, int, string> _onChange;
        private readonly Action<DirectoryChangeWatch> _onOverflow;
        private readonly Action<string> _warn;
        private readonly ManualResetEvent _ioCompleted = new ManualResetEvent(false);
        private readonly ManualResetEvent _stopRequested = new ManualResetEvent(false);
        private readonly WaitHandle[] _waitHandles;
        private readonly object _closeGate = new object();
        private bool _closed;
        private bool _ioPending;
        private IntPtr _handle = InvalidHandle;
        private IntPtr _buffer;
        private IntPtr _overlapped;

        private DirectoryChangeWatch(string directory, bool recursive, Action<DirectoryChangeWatch, int, string> onChange,
                                     Action<DirectoryChangeWatch> onOverflow, Action<string> warn)
        {
            Directory = directory;
            Recursive = recursive;
            _onChange = onChange;
            _onOverflow = onOverflow;
            _warn = warn;
            _waitHandles = new WaitHandle[] { _ioCompleted, _stopRequested };
        }

        public static DirectoryChangeWatch Start(string directory, bool recursive, Action<DirectoryChangeWatch, int, string> onChange,
                                                 Action<DirectoryChangeWatch> onOverflow, Action<string> warn)
        {
            var watch = new DirectoryChangeWatch(directory, recursive, onChange, onOverflow, warn);
            watch._handle = CreateFileW(directory, FileListDirectory, FileShareAll, IntPtr.Zero, OpenExisting, BackupSemanticsOverlapped, IntPtr.Zero);
            if (watch._handle == InvalidHandle)
            {
                warn($"cannot watch {directory} (Win32 error {Marshal.GetLastWin32Error()})");
                return null;
            }
            watch._buffer = Marshal.AllocHGlobal(BufferBytes);
            watch._overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
            new Thread(watch.Run) { Name = "Fires file watch", IsBackground = true }.Start();
            return watch;
        }

        public void Stop()
        {
            lock (_closeGate)
            {
                if (!_closed) _stopRequested.Set();
            }
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    _ioCompleted.Reset();
                    Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _ioCompleted.SafeWaitHandle.DangerousGetHandle() }, _overlapped, false);
                    if (!ReadDirectoryChangesW(_handle, _buffer, BufferBytes, Recursive, NotifyFilter, IntPtr.Zero, _overlapped, IntPtr.Zero))
                    {
                        _warn($"stopped watching {Directory} (Win32 error {Marshal.GetLastWin32Error()})");
                        return;
                    }
                    _ioPending = true;
                    if (WaitHandle.WaitAny(_waitHandles) != IoCompletedIndex) return;
                    _ioPending = false;
                    if (!GetOverlappedResult(_handle, _overlapped, out uint bytes, false))
                    {
                        _warn($"stopped watching {Directory} (Win32 error {Marshal.GetLastWin32Error()})");
                        return;
                    }
                    if (bytes == 0) _onOverflow(this);
                    else Report();
                }
            }
            catch (ThreadAbortException)
            {
            }
            catch (Exception ex)
            {
                _warn($"stopped watching {Directory}: {ex}");
            }
            finally
            {
                Close();
            }
        }

        // The kernel writes into the buffer until a pending read is cancelled and has completed, so only then is it freed.
        private void Close()
        {
            lock (_closeGate)
            {
                _closed = true;
                if (_ioPending)
                {
                    CancelIoEx(_handle, _overlapped);
                    GetOverlappedResult(_handle, _overlapped, out _, true);
                }
                CloseHandle(_handle);
                Marshal.FreeHGlobal(_buffer);
                Marshal.FreeHGlobal(_overlapped);
                _ioCompleted.Close();
                _stopRequested.Close();
            }
        }

        private void Report()
        {
            int offset = 0;
            while (true)
            {
                int next = Marshal.ReadInt32(_buffer, offset + NextEntryOffset);
                int action = Marshal.ReadInt32(_buffer, offset + ActionOffset);
                int nameBytes = Marshal.ReadInt32(_buffer, offset + NameLengthOffset);
                _onChange(this, action, Marshal.PtrToStringUni(IntPtr.Add(_buffer, offset + NameOffset), nameBytes / BytesPerChar));
                if (next == 0) return;
                offset += next;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition,
                                                 uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadDirectoryChangesW(IntPtr directory, IntPtr buffer, uint bufferLength, bool watchSubtree,
                                                         uint notifyFilter, IntPtr bytesReturned, IntPtr overlapped, IntPtr completionRoutine);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped, out uint bytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
