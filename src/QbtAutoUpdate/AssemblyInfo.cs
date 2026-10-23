using System.Runtime.CompilerServices;

// All interop goes through source generators ([LibraryImport], [GeneratedComInterface]) with explicit
// types, which is also what lets them pass VARIANTs (ComVariant) by value to Task Scheduler.
[assembly: DisableRuntimeMarshalling]
