// The main entry of the package: the view model, the three views, and the helpers that format numbers. It runs in
// browsers; the view model also runs in Node.
export {
  DEFAULT_SMALL_FILE_SIZE, STACK_COMPONENTS, buildModel, columnTree, commitsPerDay, deletedEstimate, findPath, quartiles,
  ratio, readFootprint, smallShareAtLeast, sortTables, stackOf,
} from "./model.js";
export type {
  CollectionModel, CollectionTotals, ColumnModel, FileSizesModel, FootprintData, MaintenanceModel, ModelOptions,
  ReadOptions, SmallFilesModel, SourceModel, StackComponent, StackComponentKey, StackModel, StorageModel, TableMeasures,
  TableModel, TableOrder, TreeNode, TreeNodeKind, VOrderModel,
} from "./model.js";
export { MISSING, formatBytes, formatCount, formatNumber, formatShare, formatTime } from "./format.js";
export type { BaseOptions, ViewHandle, ViewTheme } from "./dom.js";
