using System;
using System.IO;
using System.Runtime.CompilerServices;
using OpenUtau.Core;

namespace OpenUtau.Test {
    /// <summary>
    /// Keeps OpenUtau's data folder in a temp folder of its own for the whole run. Saving a
    /// project writes prefs.json, which would otherwise be the user's.
    /// </summary>
    internal static class TestDataPath {
        [ModuleInitializer]
        internal static void Init() {
            string path = Path.Combine(Path.GetTempPath(), "OpenUtauTest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            PathManager.Inst.SetDataPathForTest(path);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => {
                try {
                    Directory.Delete(path, recursive: true);
                } catch (Exception) {
                    // A leftover temp folder is not worth failing a run over.
                }
            };
        }
    }
}
