using System;
using System.Linq;

namespace SourdoughMonitor.Models;

public sealed record Sample(DateTimeOffset Time, double RisePercent);