namespace Maus.Bench.Gpu;

public interface ITexture : IDisposable
{
    TextureDesc Desc { get; }
}

public interface IBuffer : IDisposable
{
    BufferDesc Desc { get; }
}

public interface IPipeline : IDisposable
{
    GraphicsPipelineDesc Desc { get; }
}

public interface IComputePipeline : IDisposable
{
    string Name { get; }
}

/// <summary>
/// Carte graphique et sa fenêtre : création des ressources, enregistrement des commandes, présentation.
/// Une seule liste de commandes (comme le contexte immédiat de Direct3D 11) ; le moteur Direct3D 12 gère lui-même
/// les barrières de ressources, les descripteurs et les images en vol, pour que les scènes soient identiques.
/// </summary>
public interface IGpuDevice : IDisposable
{
    GpuApi Api { get; }

    string AdapterName { get; }

    /// <summary>Mémoire vidéo dédiée annoncée par la carte (octets).</summary>
    long DedicatedVideoMemory { get; }

    /// <summary>Taille de l'image affichée (la fenêtre, ou l'image hors écran des captures).</summary>
    int OutputWidth { get; }

    int OutputHeight { get; }

    /// <summary>Image de la fenêtre à dessiner pour l'image en cours (change à chaque présentation).</summary>
    ITexture BackBuffer { get; }

    ICommandList Commands { get; }

    /// <summary>Durée de la dernière image mesurée par la carte elle-même (horodatages), avec quelques images de retard.</summary>
    double LastGpuFrameMilliseconds { get; }

    ITexture CreateTexture(TextureDesc desc);

    /// <summary>
    /// Remplit un niveau de détail d'une texture depuis la mémoire du processeur (chargement, hors mesure).
    /// <paramref name="slice"/> = face d'un cube ; pour une texture 3D, <paramref name="data"/> contient toutes les tranches.
    /// </summary>
    void UploadTexture(ITexture texture, int mip, int slice, ReadOnlySpan<byte> data, int rowPitch);

    IBuffer CreateBuffer(BufferDesc desc, ReadOnlySpan<byte> initialData = default);

    IPipeline CreatePipeline(GraphicsPipelineDesc desc);

    IComputePipeline CreateComputePipeline(ShaderCode computeShader);

    /// <summary>Début d'une image : à appeler avant d'enregistrer des commandes.</summary>
    void BeginFrame();

    /// <summary>Fin d'une image : soumet les commandes et affiche l'image (sans attendre la synchronisation verticale).</summary>
    void Present();

    /// <summary>Attend que la carte ait terminé tout le travail soumis (fin d'une mesure, capture).</summary>
    void WaitIdle();

    /// <summary>Copie l'image affichée en mémoire (RVBA 8 bits par composante) : captures de contrôle.</summary>
    byte[] CaptureBackBuffer(out int width, out int height);

    /// <summary>
    /// Copie en mémoire le premier niveau d'une texture 2D RVBA 8 bits (vignettes des scènes, image du résultat). Attend
    /// que la carte ait fini : à n'appeler qu'en dehors des mesures.
    /// </summary>
    byte[] ReadTexture(ITexture texture);
}

/// <summary>Commandes de rendu, dans l'ordre où la carte les exécutera.</summary>
public interface ICommandList
{
    void SetRenderTargets(ReadOnlySpan<ITexture> colors, ITexture? depth, int mip = 0, int slice = 0);

    void SetRenderTarget(ITexture? color, ITexture? depth = null, int mip = 0, int slice = 0);

    void SetViewport(float x, float y, float width, float height);

    /// <summary>Rectangle de découpe (toujours actif) : à appeler après chaque changement de cible.</summary>
    void SetScissor(int left, int top, int right, int bottom);

    void Clear(ITexture target, ColorF color, int mip = 0, int slice = 0);

    void ClearDepth(ITexture depth, float value);

    void SetPipeline(IPipeline pipeline);

    void SetComputePipeline(IComputePipeline pipeline);

    /// <summary>Constantes du registre b<paramref name="slot"/> (0 ou 1), au plus 4 096 octets.</summary>
    void SetConstants<T>(int slot, in T data)
        where T : unmanaged;

    /// <summary>Texture lue par les shaders (registre t<paramref name="slot"/>, 0 à 15) ; <paramref name="mip"/> = -1 pour tous les niveaux.</summary>
    void SetTexture(int slot, ITexture? texture, int mip = -1);

    /// <summary>Tampon lu par les shaders (registre t<paramref name="slot"/>).</summary>
    void SetBuffer(int slot, IBuffer? buffer);

    /// <summary>Texture écrite par un shader de calcul (registre u<paramref name="slot"/>, 0 à 7).</summary>
    void SetStorageTexture(int slot, ITexture? texture, int mip = 0);

    void SetStorageBuffer(int slot, IBuffer? buffer);

    void SetVertexBuffer(int slot, IBuffer? buffer, int stride);

    /// <summary>Sommets valables pour cette image seulement (interface, textes) : copiés dans un tampon tournant.</summary>
    void SetTransientVertices<T>(int slot, ReadOnlySpan<T> vertices)
        where T : unmanaged;

    void SetIndexBuffer(IBuffer? buffer);

    void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0);

    void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int baseVertex = 0, int firstInstance = 0);

    /// <summary>Dessin dont les paramètres (5 entiers) ont été écrits par la carte elle-même à <paramref name="offsetBytes"/>.</summary>
    void DrawIndexedIndirect(IBuffer arguments, int offsetBytes);

    void Dispatch(int groupsX, int groupsY, int groupsZ);

    /// <summary>Remplace une partie d'un tampon (données venues du processeur).</summary>
    void UpdateBuffer<T>(IBuffer buffer, ReadOnlySpan<T> data, int offsetBytes = 0)
        where T : unmanaged;

    /// <summary>Copie une texture entière dans une autre de même taille et de même format.</summary>
    void CopyTexture(ITexture destination, ITexture source);

    /// <summary>
    /// Envoie à la carte le travail déjà enregistré. Les scènes très lourdes découpent l'image en bandes et envoient
    /// chaque bande séparément : aucune commande ne dure assez longtemps pour que Windows croie la carte bloquée.
    /// </summary>
    void Flush();
}
