```

BenchmarkDotNet v0.14.0, macOS Sonoma 14.6 (23G80) [Darwin 23.6.0]
Apple M1, 1 CPU, 8 logical and 8 physical cores
.NET SDK 8.0.403
  [Host]     : .NET 8.0.10 (8.0.1024.46610), Arm64 RyuJIT AdvSIMD
  DefaultJob : .NET 8.0.10 (8.0.1024.46610), Arm64 RyuJIT AdvSIMD


```
| Method | Mean      | Error     | StdDev    |
|------- |----------:|----------:|----------:|
| Sha256 |  4.230 μs | 0.0305 μs | 0.0271 μs |
| Md5    | 19.090 μs | 0.1158 μs | 0.1083 μs |
