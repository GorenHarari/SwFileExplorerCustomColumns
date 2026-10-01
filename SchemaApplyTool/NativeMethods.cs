using System.Runtime.InteropServices;

namespace SchemaApplyTool
{
    // propsys.dll's property schema (de)registration functions - the same
    // mechanism SwFilterDump's --refresh-schema mode already proved out
    // against SolidWorks's own .propdesc file.
    internal static class NativeMethods
    {
        [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int PSRegisterPropertySchema(string pszPath);

        [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int PSUnregisterPropertySchema(string pszPath);

        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(int wEventId, uint uFlags, System.IntPtr dwItem1, System.IntPtr dwItem2);

        // SHCNE_ASSOCCHANGED / SHCNF_IDLIST - broad "something about file
        // associations/handlers changed" notification.
        public const int SHCNE_ASSOCCHANGED = 0x08000000;
        public const uint SHCNF_IDLIST = 0x0000;
    }
}
