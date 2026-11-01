using OpenUtau.Core.Util;
using Xunit;

namespace OpenUtau.Core {
    /// <summary>
    /// A host that runs OpenUtau inside its own process can't survive a GPU runner's native aborts, such as the
    /// WebGPU plugin's, so it pins the runner. The pin must win over the saved preference without clearing it, since
    /// the host may share its preferences with the standalone OpenUtau.
    /// </summary>
    [Collection(RenderSingletonCollection.Name)]
    public class OnnxRunnerOverrideTest {
        [Fact]
        public void AnOverrideWinsOverThePreferenceAndKeepsIt() {
            var previousRunner = Preferences.Default.OnnxRunner;
            var previousOverride = Onnx.RunnerOverride;
            try {
                Preferences.Default.OnnxRunner = "WebGPU";
                Onnx.RunnerOverride = "CPU";
                Assert.Equal(new[] { "CPU" }, Onnx.getRunnerOptions());
                Assert.True(Onnx.IsCpuRunner());
                Assert.False(Onnx.IsWebGpuRunner());
                Assert.False(Onnx.IsDmlRunner());
                // Loading the preferences keeps the runner chosen in the standalone OpenUtau.
                Assert.True(Onnx.KeepsRunnerPreference("WebGPU"));
                Assert.Equal("WebGPU", Preferences.Default.OnnxRunner);
            } finally {
                Onnx.RunnerOverride = previousOverride;
                Preferences.Default.OnnxRunner = previousRunner;
            }
        }

        [Fact]
        public void WithoutAnOverrideAnUnavailableRunnerIsDropped() {
            var previousOverride = Onnx.RunnerOverride;
            try {
                Onnx.RunnerOverride = null;
                Assert.Contains("CPU", Onnx.getRunnerOptions());
                Assert.True(Onnx.KeepsRunnerPreference("CPU"));
                Assert.False(Onnx.KeepsRunnerPreference("NoSuchRunner"));
            } finally {
                Onnx.RunnerOverride = previousOverride;
            }
        }
    }
}
