using Microsoft.Win32;
using System;

namespace Droute.Installer.Classes
{
    internal static class RuntimeToggle
    {
        private const string RegistryPath = @"Software\droute";
        private const string ValueName = "enabled";
        private const string DeadlineValueName = "enabled_until";
        private const string SessionHostValueName = "session_host";
        private const string SessionPortValueName = "session_port";
        private const string SessionUserValueName = "session_user";
        private const string SessionPassValueName = "session_pass";

        public static readonly TimeSpan ActivationDuration = TimeSpan.FromMinutes(5);

        /// <summary>Margin so the local window always ends before the server drops the credential.</summary>
        private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
            {
                object value = key?.GetValue(ValueName, 0);
                if (!(value is int) || (int)value == 0)
                    return false;

                long deadline = ReadDeadline(key);
                if (deadline > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    return true;
            }

            // A missing or expired deadline is never allowed to leave the proxy on.
            SetEnabled(false);
            return false;
        }

        /// <summary>
        /// Publishes the credential for this activation and turns the proxy on.
        ///
        /// The local deadline written here is a convenience for the user interface,
        /// not a security control: a modified client can ignore it. What actually
        /// bounds the activation is the credential's own lifetime on the server,
        /// which expires whether or not this process cooperates.
        /// </summary>
        public static void Activate(SessionCredential credential)
        {
            if (credential == null)
                throw new ArgumentNullException(nameof(credential));

            TimeSpan window = ActivationDuration;
            if (credential.ExpiresIn > TimeSpan.Zero)
            {
                TimeSpan serverWindow = credential.ExpiresIn - ExpiryMargin;
                if (serverWindow < window)
                    window = serverWindow;
            }
            if (window <= TimeSpan.Zero)
                throw new InvalidOperationException("A credencial recebida já estava expirada.");

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                // Credential first, then the deadline, then the switch. The native
                // poller reads the switch last, so it never observes an activation
                // it cannot complete.
                key.SetValue(SessionHostValueName, credential.Host ?? string.Empty, RegistryValueKind.String);
                key.SetValue(SessionPortValueName, credential.Port, RegistryValueKind.DWord);
                key.SetValue(SessionUserValueName, credential.Username, RegistryValueKind.String);
                key.SetValue(SessionPassValueName, credential.Password, RegistryValueKind.String);

                long deadline = DateTimeOffset.UtcNow.Add(window).ToUnixTimeSeconds();
                key.SetValue(DeadlineValueName, deadline, RegistryValueKind.QWord);
                key.SetValue(ValueName, 1, RegistryValueKind.DWord);
            }
        }

        public static void SetEnabled(bool enabled)
        {
            if (enabled)
                throw new InvalidOperationException(
                    "Activate(credential) é o único caminho para ligar o proxy.");

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                key.SetValue(ValueName, 0, RegistryValueKind.DWord);
                key.DeleteValue(DeadlineValueName, false);
                ClearCredential(key);
            }
        }

        /// <summary>Removes every trace of the activation, including the identity file.</summary>
        public static void Purge()
        {
            SetEnabled(false);
            DeviceIdentity.Remove();
        }

        public static TimeSpan GetRemaining()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath, false))
            {
                object value = key?.GetValue(ValueName, 0);
                if (!(value is int) || (int)value == 0)
                    return TimeSpan.Zero;

                long remainingSeconds = ReadDeadline(key) - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return remainingSeconds > 0 ? TimeSpan.FromSeconds(remainingSeconds) : TimeSpan.Zero;
            }
        }

        private static void ClearCredential(RegistryKey key)
        {
            key.DeleteValue(SessionUserValueName, false);
            key.DeleteValue(SessionPassValueName, false);
            key.DeleteValue(SessionHostValueName, false);
            key.DeleteValue(SessionPortValueName, false);
        }

        private static long ReadDeadline(RegistryKey key)
        {
            object value = key?.GetValue(DeadlineValueName, 0L);
            return value is long ? (long)value : 0L;
        }
    }
}
