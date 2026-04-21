namespace RevitGltfExporter.UI
{
    public class ExportOptions
    {
        public bool EnableDraco { get; set; } = false;
        public int DracoCompressionLevel { get; set; } = 7;
        public bool IncludeProperties { get; set; } = true;
    }
}
