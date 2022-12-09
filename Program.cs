﻿using System.Runtime.InteropServices;
using System.Text;

namespace efiboot
{
    [Flags]
    internal enum ExitWindows : uint
    {
        Reboot = 0x02,
    }

    [Flags]
    internal enum ShutdownReason : uint
    {
        MajorOther = 0x00000000,
        MinorOther = 0x00000000,
        FlagPlanned = 0x80000000
    }

    internal enum FirmwareType : uint
    {
        FirmwareTypeUnknown = 0,
        FirmwareTypeBios = 1,
        FirmwareTypeUefi = 2,
        FirmwareTypeMax = 3
    }

    static class ErrorCodes
    {
        public const int ERROR_PRIVILEGE_NOT_HELD = 1314;
        public const int ERROR_NOACCESS = 998;

    }

    internal class NativeMethods
    {
        [DllImport("kernel32.dll")]
        private static extern bool GetFirmwareType(ref FirmwareType FirmwareType);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
        public static extern uint GetFirmwareEnvironmentVariable([MarshalAs(UnmanagedType.LPWStr)] string lpName, [MarshalAs(UnmanagedType.LPWStr)] string lpGuid, byte[] pBuffer, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
        public static extern bool SetFirmwareEnvironmentVariable([MarshalAs(UnmanagedType.LPWStr)] string lpName, [MarshalAs(UnmanagedType.LPWStr)] string lpGuid, byte[] pValue, uint nSize);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern bool AdjustTokenPrivileges(IntPtr htok, bool disall, ref PrivelegeHelper.TokenPrivelege newst, int len, IntPtr prev, IntPtr relen);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr h, int acc, ref IntPtr phtok);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool LookupPrivilegeValue(string host, string name, ref long pluid);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ExitWindowsEx(ExitWindows uFlags, ShutdownReason dwReason);

        public static FirmwareType GetFirmwareType()
        {
            FirmwareType type = FirmwareType.FirmwareTypeUnknown;
            if (GetFirmwareType(ref type))
            {
                return type;
            }
            return FirmwareType.FirmwareTypeUnknown;
        }
    }

    internal static class EFIEnvironment
    {
        private const string EFI_GLOBAL_VARIABLE = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";
        private const string EFI_TEST_VARIABLE = "{00000000-0000-0000-0000-000000000000}";
        private const string EFI_BOOT_ORDER = "BootOrder";
        private const string EFI_BOOT_CURRENT = "BootCurrent";
        private const string EFI_BOOT_NEXT = "BootNext";
        private const string LOAD_OPTION_FORMAT = "Boot{0:X4}";

        public static string IsSupported()
        {
            if (NativeMethods.GetFirmwareType() != FirmwareType.FirmwareTypeUefi)
                return "Not in UEFI mode";

            if (NativeMethods.GetFirmwareEnvironmentVariable(string.Empty, EFI_TEST_VARIABLE, null, 0) == 0)
                return Marshal.GetLastWin32Error() switch
                {
                    ErrorCodes.ERROR_NOACCESS => null,
                    ErrorCodes.ERROR_PRIVILEGE_NOT_HELD => "Elevation required",
                    int err => $"Unknown error: {err}"
                };

            return null;
        }

        public static uint ReadVariable(string name, out byte[] buffer)
        {
            buffer = new byte[10000];
            return NativeMethods.GetFirmwareEnvironmentVariable(name, EFI_GLOBAL_VARIABLE, buffer, (uint)buffer.Length);
        }

        public static BootEntry[] GetEntries()
        {
            var length = (int)ReadVariable(EFI_BOOT_ORDER, out var buffer);
            List<BootEntry> entries = new List<BootEntry>();
            ushort currentEntryId = GetBootCurrent();
            ushort nextEntryId = GetBootNext();
            return Enumerable.Range(0, length / 2).Select(x => BitConverter.ToUInt16(buffer, x * 2)).Select(optionId => new BootEntry()
            {
                Id = optionId,
                Description = GetDescription(optionId),
                IsCurrent = currentEntryId == optionId,
                IsBootNext = nextEntryId == optionId
            }).ToArray();
        }

        public static string GetDescription(ushort optionId)
        {
            var length = (int)ReadVariable(string.Format(LOAD_OPTION_FORMAT, optionId), out var buffer);
            return new string(Encoding.Unicode.GetString(buffer, 6, length).TakeWhile(x => x != 0).ToArray());
        }

        public static ushort GetBootCurrent()
        {
            uint length = ReadVariable(EFI_BOOT_CURRENT, out var buffer);
            if (length == 0)
                throw new Exception("Error" + Marshal.GetLastWin32Error());
            return BitConverter.ToUInt16(buffer, 0);
        }

        public static ushort GetBootNext()
        {
            uint length = ReadVariable(EFI_BOOT_NEXT, out var buffer);
            if (length == 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 203)
                    throw new Exception("Error" + err);
            }
            return BitConverter.ToUInt16(buffer, 0);
        }

        public static void SetVariable(string name, byte[] buffer)
        {
            bool result = NativeMethods.SetFirmwareEnvironmentVariable(name, EFI_GLOBAL_VARIABLE, buffer, (uint)buffer.Length);
            if (!result)
                throw new InvalidOperationException("Unable to set variable");
        }

        public static void SetBootNext(ushort entryId)
        {
            SetVariable(EFI_BOOT_NEXT, BitConverter.GetBytes(entryId));
        }

        public static void SetBootNext(BootEntry entry)
        {
            SetBootNext(entry.Id);
        }
    }

    internal class BootEntry
    {
        internal ushort Id;
        internal string Description;
        internal bool IsCurrent;
        internal bool IsBootNext;
    }

    internal class PrivelegeHelper
    {

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct TokenPrivelege
        {
            public int Count;
            public long Luid;
            public int Attr;
        }

        private const string SE_SYSTEM_ENVIRONMENT_NAME = "SeSystemEnvironmentPrivilege";
        private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";
        internal const int SE_PRIVILEGE_ENABLED = 0x00000002;
        internal const int TOKEN_QUERY = 0x00000008;
        internal const int TOKEN_ADJUST_PRIVILEGES = 0x00000020;

        public static void ObtainPrivileges(string privilege)
        {
            IntPtr hToken = IntPtr.Zero;

            if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, ref hToken))
            {
                throw new InvalidOperationException("OpenProcessToken failed!");
            }

            TokenPrivelege tp;
            tp.Count = 1;
            tp.Luid = 0;
            tp.Attr = SE_PRIVILEGE_ENABLED;

            if (!NativeMethods.LookupPrivilegeValue(null, privilege, ref tp.Luid))
                throw new InvalidOperationException("LookupPrivilegeValue failed!");
            if (!NativeMethods.AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("AdjustTokenPrivileges failed!");
        }

        public static void ObtainSystemPrivileges()
        {
            ObtainPrivileges(SE_SYSTEM_ENVIRONMENT_NAME);
            ObtainPrivileges(SE_SHUTDOWN_NAME);
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            PrivelegeHelper.ObtainSystemPrivileges();
            var err = EFIEnvironment.IsSupported();
            if (err != null)
            {
                Console.WriteLine(err);
                return;
            }

            var res = EFIEnvironment.GetEntries();
            bool reboot;
            BootEntry bootNext = null;
            string str;

            if (args.Length != 1)
            {
                Console.WriteLine("Command line usage: efiboot [<boot item name>|<boot item number>[!]]");
                Console.WriteLine("Example: efiboot ubuntu!");
                Console.WriteLine("Example: efiboot \"Windows Boot Manager\"");
                Console.WriteLine("Example: efiboot 3!");
                Console.WriteLine("! will restart the system after setting the boot next parameter");

                Console.WriteLine();
                Console.WriteLine("------------- Available options ----------------");
                foreach (var item in res)
                    Console.WriteLine(item.Id + ": " + item.Description + (item.IsBootNext ? " [next]" : "") + (item.IsCurrent ? " [current]" : ""));
                Console.WriteLine("------------------------------------------------");

                Console.WriteLine("Choose boot next record number (add ! to reboot now) or just press <ENTER> to exit:");
                str = Console.ReadLine();
            }
            else
                str = args[0];

            reboot = str.EndsWith("!");
            str = str.TrimEnd('!');
            bootNext = int.TryParse(str, out var id) ? res.FirstOrDefault(x => x.Id == id) : res.FirstOrDefault(x => x.Description == str);

            if (bootNext != null)
            {
                EFIEnvironment.SetBootNext(bootNext);
                Console.WriteLine("Next boot will be: " + bootNext.Description);
                Console.WriteLine("Reboot: " + reboot);
                if (reboot && !NativeMethods.ExitWindowsEx(ExitWindows.Reboot, ShutdownReason.MajorOther | ShutdownReason.MinorOther | ShutdownReason.FlagPlanned))
                    Console.WriteLine("Shutdown failed");
            }
            else
                Console.WriteLine("Entry not found!");
        }
    }
}
