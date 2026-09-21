# Current dashboard workflow

1. Open the [managed dashboard](http://127.0.0.1:5000/), refresh the process list and connect the desired existing TIA process. Approve access in TIA if prompted.
2. Select List devices. A single station is selected automatically; if there are several, choose the desired Device by name.
3. Select Read device. A single CPU is selected automatically; if there are several, choose the CPU by name.
4. Select List blocks or List UDTs, then choose the item by its path/name in the corresponding dropdown.
5. Select Read block or Read UDT. The default is metadata plus the best available native source. Each row has separate source/path controls: clear Include source (or Include UDT source) for metadata only and the matching path checkbox to skip optional path construction.

For blocks, source format best follows the native language/type. For UDTs, best tries external-source (.udt), then SIMATIC SD, then SimaticML. An explicit external-source, simatic-sd or simatic-ml request never falls back. Include dependencies is available only with source enabled and explicit external-source.

The JSON result preserves native content. Metadata-only results have source:null. Source failure also has source:null but records errors and retains metadata. Earlier failed attempts followed by successful fallback appear in Connection state and recent events; they do not become result errors.

No manual ID copying is needed for the normal workflow. The raw Device/CPU fields remain for selector diagnostics. Read device takes a station Device ID; List blocks takes the CPU's plcObjectId; Read block takes the chosen block's objectId. Each belongs to its selected process/connection. Reconnection clears all selections and cached choices.

A reconnectRequired failure requires explicit reconnection. Ordinary selector or export failure does not invalidate a still-valid connection. See [get_block verification](get-block.md) and [the transition boundary](rehaul-transition.md). MCP execution remains held until the complete eleven-tool cutover.
