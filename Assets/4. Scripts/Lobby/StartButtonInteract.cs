using UnityEngine;

public class StartButtonInteract : MonoBehaviour, IInteractable
{
    public LobbyTerminal lobbyTerminal;
    public string promptMessage = "게임 시작";

    public void OnInteract()
    {
        if (lobbyTerminal != null) lobbyTerminal.InteractStartGame();
    }

    public string GetInteractPrompt()
    {
        return promptMessage;
    }
}