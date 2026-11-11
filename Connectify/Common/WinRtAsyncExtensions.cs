using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace bluetooth_turnon
{
    /// <summary>
    /// Thread-safe adapter to await Windows Runtime IAsyncOperation and IAsyncAction.
    /// Handles synchronous completion, error states, and cancellations without deadlocking.
    /// </summary>
    internal static class WinRtAsyncExtensions
    {
        public static Task<T> AsTask<T>(this IAsyncOperation<T> asyncOp, CancellationToken ct = default)
        {
            if (asyncOp == null) throw new ArgumentNullException(nameof(asyncOp));

            if (asyncOp.Status == AsyncStatus.Completed)
                return Task.FromResult(asyncOp.GetResults());
            if (asyncOp.Status == AsyncStatus.Error)
            {
                var t = new TaskCompletionSource<T>();
                t.TrySetException(asyncOp.ErrorCode ?? new Exception("WinRT error."));
                return t.Task;
            }
            if (asyncOp.Status == AsyncStatus.Canceled)
            {
                var t = new TaskCompletionSource<T>();
                t.TrySetCanceled();
                return t.Task;
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration ctr = default;
            if (ct.CanBeCanceled)
            {
                ctr = ct.Register(() =>
                {
                    try { asyncOp.Cancel(); } catch { }
                    // Allow WinRT 500ms to invoke Completed gracefully; fallback to ensure caller is unblocked if WinRT hangs
                    Task.Delay(500).ContinueWith(_ =>
                    {
                        try { ctr.Dispose(); } catch { }
                        tcs.TrySetCanceled();
                    });
                });
            }

            tcs.Task.ContinueWith(_ =>
            {
                try { ctr.Dispose(); } catch { }
            }, TaskContinuationOptions.ExecuteSynchronously);

            asyncOp.Completed = (info, status) =>
            {
                try { ctr.Dispose(); } catch { }
                switch (status)
                {
                    case AsyncStatus.Completed:
                        T res = default;
                        try { res = info.GetResults(); } catch { }
                        if (ct.IsCancellationRequested)
                        {
                            // Cancellation was requested while the operation completed.
                            // Immediately dispose the result so it doesn't leak or hold the ACL link open,
                            // then signal cancellation on tcs.
                            DisposeWinRtObject(res);
                            tcs.TrySetCanceled();
                        }
                        else if (!tcs.TrySetResult(res))
                        {
                            DisposeWinRtObject(res);
                        }
                        break;
                    case AsyncStatus.Canceled:
                        tcs.TrySetCanceled();
                        break;
                    case AsyncStatus.Error:
                        if (ct.IsCancellationRequested)
                        {
                            tcs.TrySetCanceled();
                        }
                        else
                        {
                            tcs.TrySetException(info.ErrorCode ?? new Exception("WinRT error."));
                        }
                        break;
                }
            };
            return tcs.Task;
        }

        public static Task AsTask(this IAsyncAction asyncAction, CancellationToken ct = default)
        {
            if (asyncAction == null) throw new ArgumentNullException(nameof(asyncAction));

            if (asyncAction.Status == AsyncStatus.Completed) return Task.FromResult(true);
            if (asyncAction.Status == AsyncStatus.Error)
            {
                var t = new TaskCompletionSource<bool>();
                t.TrySetException(asyncAction.ErrorCode ?? new Exception("WinRT error."));
                return t.Task;
            }
            if (asyncAction.Status == AsyncStatus.Canceled)
            {
                var t = new TaskCompletionSource<bool>();
                t.TrySetCanceled();
                return t.Task;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration ctr = default;
            if (ct.CanBeCanceled)
            {
                ctr = ct.Register(() =>
                {
                    try { asyncAction.Cancel(); } catch { }
                    Task.Delay(500).ContinueWith(_ =>
                    {
                        try { ctr.Dispose(); } catch { }
                        tcs.TrySetCanceled();
                    });
                });
            }

            tcs.Task.ContinueWith(_ =>
            {
                try { ctr.Dispose(); } catch { }
            }, TaskContinuationOptions.ExecuteSynchronously);

            asyncAction.Completed = (info, status) =>
            {
                try { ctr.Dispose(); } catch { }
                switch (status)
                {
                    case AsyncStatus.Completed:
                        try { info.GetResults(); } catch { }
                        if (ct.IsCancellationRequested)
                        {
                            tcs.TrySetCanceled();
                        }
                        else
                        {
                            tcs.TrySetResult(true);
                        }
                        break;
                    case AsyncStatus.Canceled:
                        tcs.TrySetCanceled();
                        break;
                    case AsyncStatus.Error:
                        if (ct.IsCancellationRequested)
                        {
                            tcs.TrySetCanceled();
                        }
                        else
                        {
                            tcs.TrySetException(info.ErrorCode ?? new Exception("WinRT error."));
                        }
                        break;
                }
            };
            return tcs.Task;
        }

        private static void DisposeWinRtObject(object obj)
        {
            if (obj == null) return;
            try
            {
                if (obj is Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceServicesResult gattResult)
                {
                    if (gattResult.Services != null)
                    {
                        foreach (var s in gattResult.Services)
                        {
                            try { s.Dispose(); } catch { }
                        }
                    }
                }
                else if (obj is Windows.Devices.Bluetooth.Rfcomm.RfcommDeviceServicesResult rfcommResult)
                {
                    if (rfcommResult.Services != null)
                    {
                        foreach (var s in rfcommResult.Services)
                        {
                            try { s.Dispose(); } catch { }
                        }
                    }
                }

                if (obj is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch { }
        }
    }
}
