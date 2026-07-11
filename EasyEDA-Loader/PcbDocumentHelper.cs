using DXP;
using EDP;
using PCB;
using System;
using System.Windows.Forms;

namespace EasyEDA_Loader
{
    public static class PcbDocumentHelper
    {
        public static IServerDocument FocusProjectPcbDocument()
        {
            var client = AltiumApi.GlobalVars.Client;
            var project = AltiumApi.GlobalVars.Workspace?.Internal_DM_FocusedProject() as IProject;
            if (project == null || client == null)
                return null;

            for (var index = 0; index < project.DM_PhysicalDocumentCount(); index++)
            {
                if (project.Internal_DM_PhysicalDocuments(index) is not IDocument document)
                    continue;

                var kind = document.DM_DocumentKind() ?? string.Empty;
                if (kind.IndexOf("PCB", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var fullPath = document.DM_FullPath();
                if (string.IsNullOrWhiteSpace(fullPath))
                    continue;

                var serverDocument = client.OpenDocument(kind, fullPath);
                if (serverDocument != null)
                {
                    client.ShowDocument(serverDocument);
                    client.SetCurrentView(serverDocument);
                    PumpUi();
                    return serverDocument;
                }
            }

            return null;
        }

        public static IPCB_Board EnsureProjectPcbBoard()
        {
            FocusProjectPcbDocument();
            PumpUi();

            var board = ResolveProjectPcbBoard();
            if (board != null)
                RefreshBoardView(board);

            return board;
        }

        public static IPCB_Board ResolveProjectPcbBoard()
        {
            var pcbServer = AltiumApi.GlobalVars.PCBServer;
            var focusedBoard = pcbServer.GetCurrentPCBBoard();
            if (focusedBoard != null)
                return focusedBoard;

            var project = AltiumApi.GlobalVars.Workspace?.Internal_DM_FocusedProject() as IProject;
            if (project == null)
                return null;

            for (var index = 0; index < project.DM_PhysicalDocumentCount(); index++)
            {
                if (project.Internal_DM_PhysicalDocuments(index) is not IDocument document)
                    continue;

                var kind = document.DM_DocumentKind() ?? string.Empty;
                if (kind.IndexOf("PCB", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var fullPath = document.DM_FullPath();
                if (string.IsNullOrWhiteSpace(fullPath))
                    continue;

                var board = pcbServer.Internal_GetPCBBoardByPath(fullPath) as IPCB_Board;
                if (board == null)
                    board = pcbServer.Internal_LoadPCBBoardByPath(fullPath) as IPCB_Board;

                if (board != null)
                    return board;
            }

            return null;
        }

        public static void RefreshBoardView(IPCB_Board board)
        {
            if (board == null)
                return;

            board.GraphicallyInvalidate();
            try
            {
                board.ViewManager_FullUpdate();
            }
            catch
            {
                // Older API builds may not expose full update; invalidate is enough.
            }

            PumpUi();
        }

        public static void PumpUi()
        {
            try
            {
                Application.DoEvents();
            }
            catch
            {
                // Best effort only.
            }
        }
    }
}
