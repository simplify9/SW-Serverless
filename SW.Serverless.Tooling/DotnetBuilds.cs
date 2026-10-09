using System.Diagnostics;

namespace SW.Serverless.Tooling
{
    /// <summary>
    /// How the tools run dotnet builds. MSBuild leaves worker nodes and a build server running for
    /// the next build, and they inherit the build's redirected output: a caller waiting for that
    /// output to end — as WaitForExit and ReadToEnd do — then waits for processes that never exit,
    /// and a build that finished in seconds hangs for good. So nothing is left running.
    /// </summary>
    public static class DotnetBuilds
    {
        public static ProcessStartInfo WithoutLingeringNodes(this ProcessStartInfo startInfo)
        {
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            startInfo.Environment["UseSharedCompilation"] = "false";
            if (!startInfo.Arguments.Contains("-nodeReuse") && startInfo.ArgumentList.Count == 0)
                startInfo.Arguments += " -nodeReuse:false";
            return startInfo;
        }
    }
}
