using Microsoft.VisualStudio.TestTools.UnitTesting;

// Timing-sensitive supervisor tests share no production state but run serially to keep their
// controlled launch/delay hand-offs deterministic under heavily loaded Windows test hosts.
[assembly: DoNotParallelize]
