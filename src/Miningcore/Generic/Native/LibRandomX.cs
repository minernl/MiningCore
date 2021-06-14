using System;
using System.Runtime.InteropServices;
using AdvancedDLSupport;
using Miningcore.Crypto.Hashing.Algorithms;

namespace Miningcore.Native
{

    public static unsafe class LibRandomX
    {
        [DllImport("randomx", EntryPoint = "randomx_get_flags", CallingConvention = CallingConvention.Cdecl)]
        public static extern Flags randomx_get_flags();

        [DllImport("randomx", EntryPoint = "randomx_alloc_cache", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr randomx_alloc_cache(Flags flags);

        [DllImport("randomx", EntryPoint = "randomx_init_cache", CallingConvention = CallingConvention.Cdecl)]
        public static extern void randomx_init_cache(IntPtr cache, byte[] key, uint keySize);

    }


    internal static class LibRandomx
    {
        public static ILibRandomx Instance { get; }

        static LibRandomx()
        {
            Instance = NativeLibraryBuilder.Default.ActivateInterface<ILibRandomx>("randomx");
        }
    }





    internal interface ILibRandomx
    {
        Flags randomx_get_flags();

        IntPtr randomx_alloc_cache(Flags flags);

        void randomx_init_cache(IntPtr cache, byte[] key, uint keySize);

        void randomx_release_cache(IntPtr cache);

        IntPtr randomx_create_vm(Flags flags, IntPtr cache, IntPtr dataset);

        void randomx_vm_set_cache(IntPtr machine, IntPtr cache);

        void randomx_destroy_vm(IntPtr machine);

        void randomx_calculate_hash(IntPtr machine, byte[] input, uint inputSize, byte[] output);

        void randomx_calculate_hash_first(IntPtr machine, byte[] input, uint inputSize);

        void randomx_calculate_hash_next(IntPtr machine, byte[] nextInput, uint nextInputSize, byte[] output);

        void randomx_calculate_hash_last(IntPtr machine, byte[] output);

    }


}
