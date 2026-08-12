using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Domain.Interfaces;

public interface IDocumentStructureInspectionStore
{
    void Save(DocumentStructureInspection inspection);

    DocumentStructureInspection? Get(Guid inspectionId);

    bool Delete(Guid inspectionId);
}
