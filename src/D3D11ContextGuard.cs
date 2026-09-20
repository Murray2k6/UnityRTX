using System;
using System.Runtime.InteropServices;

namespace UnityRemix
{
    // Unity's render thread and main-thread mesh readback share one immediate
    // context. A managed lock cannot serialize Unity's native driver calls.
    internal sealed class D3D11ContextGuard : IDisposable
    {
        private IntPtr multithread;
        private readonly ContextAction leave;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ContextAction(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetProtected(IntPtr self, int enabled);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetProtected(IntPtr self);

        private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

        public D3D11ContextGuard(IntPtr context)
        {
            var iid = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
            int result = Marshal.QueryInterface(context, ref iid, out multithread);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            try
            {
                // Keep protection enabled: restoring it while Unity's render thread
                // is using this context would reintroduce unsynchronized access.
                Method<SetProtected>(multithread, 5)(multithread, 1);
                if (Method<GetProtected>(multithread, 6)(multithread) == 0)
                    throw new NotSupportedException("D3D11 immediate context cannot enable thread protection.");
                leave = Method<ContextAction>(multithread, 4);
                Method<ContextAction>(multithread, 3)(multithread);
            }
            catch
            {
                Marshal.Release(multithread);
                multithread = IntPtr.Zero;
                throw;
            }
        }

        public void Dispose()
        {
            if (multithread == IntPtr.Zero) return;
            leave(multithread);
            Marshal.Release(multithread);
            multithread = IntPtr.Zero;
        }
    }
}
