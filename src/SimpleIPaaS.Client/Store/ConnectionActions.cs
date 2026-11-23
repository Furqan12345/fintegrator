using System;
using System.Collections.Generic;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public class LoadConnectionsAction { }
// A failed load leaves the cached list in place and stops the spinner (SafeFetch already showed a toast).
public class LoadConnectionsFailedAction { }
public class LoadConnectionsResultAction { public IEnumerable<ConnectionDto> Connections { get; set; } = Array.Empty<ConnectionDto>(); }

public class SaveConnectionAction { public ConnectionDto Connection { get; set; } = null!; }
public class SaveConnectionResultAction { public ConnectionDto Connection { get; set; } = null!; }
public class SaveConnectionFailedAction { }
