; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
GBS0002 | GBSharp.Language | Error | Unsupported type
GBS0007 | GBSharp.Language | Info | 32-bit arithmetic on SM83
GBS0042 | GBSharp.Language | Error | Dynamic collection
GBS0043 | GBSharp.Language | Error | System.String is unavailable
GBS0044 | GBSharp.Language | Error | Exceptions are unavailable
GBS0045 | GBSharp.Language | Error | Delegates and events are unavailable
GBS0046 | GBSharp.Language | Error | Interfaces are unavailable
GBS0047 | GBSharp.Language | Error | async/await is unavailable
GBS0049 | GBSharp.Language | Error | LINQ is unavailable
GBS0050 | GBSharp.Language | Error | Reference type allocation
GBS0201 | GBSharp.Memory | Info | Static allocation reserves WRAM
GBS0203 | GBSharp.Memory | Info | Static readonly data reserves ROM
