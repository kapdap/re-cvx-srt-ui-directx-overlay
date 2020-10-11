using SRTPluginBase;
using System;

namespace SRTPluginUIRECVXDirectXOverlay
{
    internal class PluginInfo : IPluginInfo
    {
        public string Name => "DirectX Overlay UI (Resident Evil: Code: Veronica)";

        public string Description => "A DirectX-based Overlay User Interface for displaying Resident Evil: Code: Veronica game memory values.";

        public string Author => "Kapdap";

        public Uri MoreInfoURL => new Uri("https://github.com/Kapdap/re-cvx-srt-ui-directx-overlay");

        public int VersionMajor => assemblyFileVersion.ProductMajorPart;

        public int VersionMinor => assemblyFileVersion.ProductMinorPart;

        public int VersionBuild => assemblyFileVersion.ProductBuildPart;

        public int VersionRevision => assemblyFileVersion.ProductPrivatePart;

        private readonly System.Diagnostics.FileVersionInfo assemblyFileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(System.Reflection.Assembly.GetExecutingAssembly().Location);
    }
}
