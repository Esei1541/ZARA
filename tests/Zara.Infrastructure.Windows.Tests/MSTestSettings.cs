using Microsoft.VisualStudio.TestTools.UnitTesting;

// Native process and window tests must not overlap each other's shared desktop observations.
[assembly: DoNotParallelize]
