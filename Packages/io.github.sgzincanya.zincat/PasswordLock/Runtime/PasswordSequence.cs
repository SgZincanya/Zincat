using System;

namespace Zincat.PasswordLock
{
    // Pure logic shared by the preview and the generated FX transition specification.
    public sealed class PasswordSequence
    {
        public const int ClearCommand = 11;
        public const int LockCommand = 12;
        public string Password { get; }
        public int Progress { get; private set; }
        public bool Unlocked { get; private set; }
        private bool held;
        private bool matches = true;

        public PasswordSequence(string password, bool unlocked = false)
        {
            if (!Valid(password)) throw new ArgumentException("密码必须是 1–20 位数字，只能使用 0–9。");
            Password = password;
            Unlocked = unlocked;
            Progress = unlocked ? password.Length : 0;
        }

        public static bool Valid(string value)
        {
            if (value == null || value.Length < 1 || value.Length > 20) return false;
            foreach (char digit in value) if (digit < '0' || digit > '9') return false;
            return true;
        }

        public static int DigitCommand(char digit) => digit - '0' + 1;

        public void Feed(int command)
        {
            if (command == 0) { held = false; return; }
            if (held) return;
            held = true;
            if (command == ClearCommand || command == LockCommand)
            { Progress = 0; Unlocked = false; matches = true; return; }
            if (Unlocked) return;
            if (command < 1 || command > 10) return;
            matches &= command == DigitCommand(Password[Progress]);
            Progress++;
            if (Progress == Password.Length)
            {
                Unlocked = matches;
                if (!matches) { Progress = 0; matches = true; }
            }
        }
    }
}
