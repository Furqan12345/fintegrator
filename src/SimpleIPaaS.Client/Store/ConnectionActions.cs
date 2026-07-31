using System;
using System.Collections.Generic;
using SimpleIPaaS.Shared.Models;

namespace SimpleIPaaS.Client.Store;

public class LoadConnectionsAction { }
public class LoadConnectionsResultAction { public IEnumerable<ConnectionDto> Connections { get; set; } = Array.Empty<ConnectionDto>(); }

public class SaveConnectionAction { public ConnectionDto Connection { get; set; } = null!; }
public class SaveConnectionResultAction { public ConnectionDto Connection { get; set; } = null!; }
public class SaveConnectionFailedAction { }
