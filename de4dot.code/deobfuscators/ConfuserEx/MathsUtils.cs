namespace de4dot.code.deobfuscators.ConfuserEx
{
    internal static class MathsUtils
    {
        const ulong MODULO32 = 0x100000000;

        public static ulong modInv(ulong num, ulong mod)
        {
            ulong a = mod, b = num % mod;
            ulong p0 = 0, p1 = 1;
            while (b != 0)
            {
                if (b == 1) return p1;
                p0 += (a / b) * p1;
                a = a % b;

                if (a == 0) break;
                if (a == 1) return mod - p0;

                p1 += (b / a) * p0;
                b = b % a;
            }
            return 0;
        }

        public static uint modInv(uint num)
        {
            return (uint)modInv(num, MODULO32);
        }

        public static byte modInv(byte num)
        {
            return (byte)modInv(num, 0x100);
        }
    }
}