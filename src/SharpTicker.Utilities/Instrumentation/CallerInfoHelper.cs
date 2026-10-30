using System.Diagnostics;
using System.IO;

namespace SharpTicker.Utilities.Instrumentation;

internal static class CallerInfoHelper
{
  /// <summary>
  /// Gets caller information by analyzing the stack trace
  /// </summary>
  /// <param name="skipFrames">Number of frames to skip from the current method</param>
  /// <returns>Formatted caller information</returns>
  public static string GetCallerInfo(int skipFrames = 4)
  {
    try
    {
      var stackTrace = new StackTrace(true);
      var frame = stackTrace.GetFrame(skipFrames);

      if (frame != null)
      {
        var method = DiagnosticMethodInfo.Create(frame);
        var fileName = frame.GetFileName();
        var lineNumber = frame.GetFileLineNumber();

        if (method != null)
        {
          var className = method.DeclaringTypeName ?? "Unknown";
          var methodName = method.Name;

          // Filter out compiler-generated methods
          if (methodName.Contains("<") || methodName.Contains(">"))
          {
            // Try to get the next frame for async methods
            var nextFrame = stackTrace.GetFrame(skipFrames + 1);
            if (nextFrame != null && DiagnosticMethodInfo.Create(nextFrame) is { } nextMethod)
            {
              className = nextMethod.DeclaringTypeName ?? className;
              methodName = nextMethod.Name;
              fileName = nextFrame.GetFileName() ?? fileName;
              lineNumber = nextFrame.GetFileLineNumber();
            }
          }

          if (!string.IsNullOrEmpty(fileName))
          {
            var shortFileName = Path.GetFileName(fileName);
            return $"{className}.{methodName} ({shortFileName}:{lineNumber})";
          }

          return $"{className}.{methodName}";
        }
      }

      return "Unknown";
    }
    catch
    {
      return "Unknown";
    }
  }

  /// <summary>
  /// Gets a simple caller name without file information
  /// </summary>
  /// <param name="skipFrames">Number of frames to skip from the current method</param>
  /// <returns>Simple caller name</returns>
  public static string GetSimpleCallerInfo(int skipFrames = 4)
  {
    try
    {
      var stackTrace = new StackTrace(false);
      var frame = stackTrace.GetFrame(skipFrames);

      if (frame != null && DiagnosticMethodInfo.Create(frame) is { } method)
      {
        var className = method.DeclaringTypeName ?? "Unknown";
        var methodName = method.Name;

        // Filter out compiler-generated methods
        if (methodName.Contains("<") || methodName.Contains(">"))
        {
          var nextFrame = stackTrace.GetFrame(skipFrames + 1);
          if (nextFrame != null && DiagnosticMethodInfo.Create(nextFrame) is { } nextMethod)
          {
            className = nextMethod.DeclaringTypeName ?? className;
            methodName = nextMethod.Name;
          }
        }

        return $"{className}.{methodName}";
      }

      return "Unknown";
    }
    catch
    {
      return "Unknown";
    }
  }
}

#if !NET9_0_OR_GREATER
/// <summary>
/// System.Diagnostics.DiagnosticMethodInfo arrived in .NET 9; on .NET 8 fall back to
/// <see cref="StackFrame.GetMethod"/> and expose the same members the helper above uses.
/// </summary>
file sealed class DiagnosticMethodInfo
{
  private DiagnosticMethodInfo(string name, string declaringTypeName)
  {
    Name = name;
    DeclaringTypeName = declaringTypeName;
  }

  public string Name { get; }

  // Full name, matching DiagnosticMethodInfo.DeclaringTypeName on .NET 9+
  public string DeclaringTypeName { get; }

  [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "Only used for diagnostic caller names; a trimmed method yields null and the caller reports \"Unknown\".")]
  public static DiagnosticMethodInfo Create(StackFrame frame)
    => frame.GetMethod() is { } method
      ? new DiagnosticMethodInfo(method.Name, method.DeclaringType?.FullName)
      : null;
}
#endif