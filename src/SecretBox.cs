using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BAMPServerBrowser
{
    /// <summary>
    /// Chiffre les secrets des fichiers de config (mots de passe, clé admin, codes de partage) avec la
    /// protection de données de Windows (DPAPI) : seul ce compte Windows sur ce PC peut les relire.
    /// Les anciennes valeurs en clair sont encore lues, puis chiffrées à la prochaine sauvegarde.
    /// </summary>
    public static class SecretBox
    {
        private const string Prefix = "dpapi1:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BAMPServerBrowser-config");
        public static string LastError = "";

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try { return Prefix + Convert.ToBase64String(Dpapi(Encoding.UTF8.GetBytes(plain), protect: true)); }
            catch (Exception ex) { LastError = ex.Message; return plain; } // jamais perdre la valeur
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // ancienne valeur en clair
            try { return Encoding.UTF8.GetString(Dpapi(Convert.FromBase64String(stored.Substring(Prefix.Length)), protect: false)); }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ModEntry.Warn("[Config] a protected value could not be read (config copied from another PC or Windows account?).");
                return "";
            }
        }

        // ------------------------------------------------------------------ crypt32
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DataBlob { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        private const int UiForbidden = 0x1;

        private static byte[] Dpapi(byte[] data, bool protect)
        {
            var hIn = GCHandle.Alloc(data, GCHandleType.Pinned);
            var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            try
            {
                var inBlob = new DataBlob { cbData = data.Length, pbData = hIn.AddrOfPinnedObject() };
                var entBlob = new DataBlob { cbData = Entropy.Length, pbData = hEnt.AddrOfPinnedObject() };
                var outBlob = new DataBlob();
                bool ok = protect
                    ? CryptProtectData(ref inBlob, "BAMPServerBrowser", ref entBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob);
                if (!ok) throw new InvalidOperationException("DPAPI error " + Marshal.GetLastWin32Error());
                try
                {
                    var res = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, res, 0, outBlob.cbData);
                    return res;
                }
                finally { LocalFree(outBlob.pbData); }
            }
            finally { hIn.Free(); hEnt.Free(); }
        }
    }
}
