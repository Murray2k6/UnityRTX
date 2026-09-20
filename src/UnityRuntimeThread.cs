using System;
#if BEPINEX6_IL2CPP
using Il2CppInterop.Runtime;
#endif

namespace UnityRemix
{
    // CoreCLR worker threads and native callbacks are not automatically known to
    // IL2CPP's collector. Only detach registrations owned by this scope.
    internal sealed class UnityRuntimeThread : IDisposable
    {
#if BEPINEX6_IL2CPP
        private IntPtr ownedThread;
#endif

        public static UnityRuntimeThread Attach()
        {
            var scope = new UnityRuntimeThread();
#if BEPINEX6_IL2CPP
            if (IL2CPP.il2cpp_thread_current() == IntPtr.Zero)
            {
                var domain = IL2CPP.il2cpp_domain_get();
                if (domain == IntPtr.Zero)
                    throw new InvalidOperationException("Unity IL2CPP domain is unavailable.");
                scope.ownedThread = IL2CPP.il2cpp_thread_attach(domain);
                if (scope.ownedThread == IntPtr.Zero)
                    throw new InvalidOperationException("Could not register the worker thread with Unity IL2CPP.");
            }
#endif
            return scope;
        }

        public static void Run(Action action)
        {
            using (Attach()) action();
        }

        public void Dispose()
        {
#if BEPINEX6_IL2CPP
            if (ownedThread != IntPtr.Zero)
            {
                IL2CPP.il2cpp_thread_detach(ownedThread);
                ownedThread = IntPtr.Zero;
            }
#endif
        }
    }
}
