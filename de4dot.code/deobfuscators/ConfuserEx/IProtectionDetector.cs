namespace de4dot.code.deobfuscators.ConfuserEx
{
    internal interface IProtectionDetector
    {
        bool Detected { get; }
        void Detect();
    }
}