# Pqax.Collector.ParquetNet

Reads Delta checkpoints for the [PQAX collector](https://github.com/pqax-org/pqax/blob/main/src/dotnet/Pqax.Collector/README.md). A checkpoint is a Parquet file with nested data, which needs a complete Parquet reader, so the collector takes it through an interface, `ICheckpointReader`, and this package implements it with [Parquet.Net](https://github.com/aloneguid/parquet-dotnet).

| | |
|---|---|
| Target | net8.0. Parquet.Net 5.6.1 ships net8.0 and net10.0 assemblies only. |
| Dependencies | `Pqax.Collector` and Parquet.Net 5.6.1. Version 5.4.0 pulls a Snappier with a known vulnerability, and version 6 changed its API. |

## Use

```csharp
var collector = new PqaxCollector(storage, new CollectorOptions(), new ParquetNetCheckpointReader());
```

`ParquetNetCheckpointReader.ReadAsync(storage, path, cancellation)` reads one checkpoint file whole, through the storage of the collection, in one request for a file under 64 MB. It returns its `add`, `remove`, `metaData`, `protocol`, `domainMetadata`, `sidecar`, and `checkpointMetadata` actions. The collector calls it for a classic checkpoint, for each part of a multi-part checkpoint, for a v2 checkpoint in Parquet, and for each sidecar file.

## What it handles

| Case | How |
|---|---|
| A required field inside an optional struct | Parquet.Net returns such a field without the rows where the struct is missing, as the Fabric warehouse writes its checkpoints. The reader lines the values up with the rows from the definition levels. |
| Maps: `partitionValues`, `tags`, `configuration` | Rebuilt row by row from the repetition and definition levels, whatever the writer names the levels between, such as `key_value` or `map` |
| Lists: `partitionColumns`, `readerFeatures`, `writerFeatures` | The same, for `list/element` and other names |
| Statistics | As JSON text in `add.stats`, which the collector parses, or as `add.stats_parsed`, from which the reader takes `numRecords` and the null counts of the top-level columns |
| Deletion vectors | Storage type, location, offset, size, and cardinality: the location and the offset identify the file in the log, and the collector keeps only the size and the cardinality |

A column that does not line up with the rows makes the read fail with an `InvalidDataException`. The collector turns it into the issue `DeltaCheckpointNotRead`.
