// HEI$Ttech
// Remote Whitelist Visibility
// v1.0.0
// 2025-05-10
// 
// This script allows you to control the visibility of a GameObject's children based on a remote whitelist.
// Currently supports: 3D Renderers, UI CanvasGroups, Canvases
// The whitelist is stored in a text file, which can be hosted:
// Disbridge (*.disbridge.com)
// GitHub (*.github.io) https://docs.github.com/en/pages/quickstart <-- Recommended
// Github Gist (gist.githubusercontent.com)
// Pastebin (pastebin.com)
// VRCDN (*.vrcdn.cloud)
// 
// Designed to be used in conjunction with the Udon String Loading script.
// https://creators.vrchat.com/worlds/udon/string-loading/
//

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.SDK3.StringLoading;
using VRC.Udon.Common.Interfaces;
using System.Linq;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class RemoteWhitelistVisibility : UdonSharpBehaviour
{
    [Header("Setup")]
    [Tooltip("The GameObject whose direct children will be controlled.")]
    public GameObject targetGameObject;

    [Tooltip("URL to the raw text file containing usernames (one per line). Use GitHub Raw or Google Drive direct download link.")]
    public VRCUrl whitelistUrl;

    [Header("Status (Read Only)")]
    [SerializeField] private bool isWhitelisted = false;
    [SerializeField] private int loadedNameCount = 0;

    // Synced variable to hold the raw whitelist text fetched by the master
    [UdonSynced] private string syncedWhitelistData = "";

    private string[] _localWhitelist = new string[0]; // Parsed whitelist for local player
    private bool _initialCheckDone = false;
    private bool _isProcessing = false; // Prevent re-entrancy

    // --- Automatic Refresh ---
    private float _refreshTimer = 0f;
    private const float REFRESH_INTERVAL = 60.0f; // Default 60 seconds
    // --- End Automatic Refresh ---

    void Start()
    {
        Log("Initializing...");
        // Disable children initially for everyone until list is checked
        SetChildrenVisibility(false);

        if (targetGameObject == null)
        {
            LogError("Target GameObject is not set!");
            return;
        }

        // Only the master attempts to download the list
        if (Networking.IsMaster)
        {
            Log("Master requesting initial whitelist download...");
            DownloadWhitelist();
        }
        else
        {
            Log("Not master, waiting for synced data...");
            // Non-masters might already have data if they join late
            // Check if data is not null or empty before processing
            if (!string.IsNullOrEmpty(syncedWhitelistData)) {
                 ProcessWhitelistData();
            }
        }
    }

    // --- VRChat Networking ---

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        // If *we* just became the master (e.g., original master left), download the list.
        // Also reset the timer if we just became master
        if (Networking.IsMaster && player == Networking.LocalPlayer)
        {
             Log("Local player became master, ensuring whitelist is downloaded.");
             _refreshTimer = 0f; // Reset timer on becoming master
             DownloadWhitelist();
        }
    }

    public override void OnDeserialization()
    {
        // Check if the synced data has actually changed and we're not already processing
        // Check against null necessary if the default value could be null
        if (!_isProcessing && syncedWhitelistData != null)
        {
            Log("Received synced data update.");
            ProcessWhitelistData();
        }
    }

    // --- Whitelist Loading ---

    public void DownloadWhitelist()
    {
        if (whitelistUrl == null || string.IsNullOrEmpty(whitelistUrl.Get()))
        {
            LogError("Whitelist URL is not set!");
            // Ensure the initial check flag is set after the first successful download attempt by master
            _initialCheckDone = true;
            return;
        }
        Log($"Attempting to download from: {whitelistUrl.Get()}");
        VRCStringDownloader.LoadUrl(whitelistUrl, (IUdonEventReceiver)this);
    }

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
        // Ensure it's our expected download before processing
        if (result.Url == null || result.Url.Get() != whitelistUrl.Get()) return;
        Log($"Raw downloaded content received:\n---\n{result.Result}\n---"); 
        Log("Whitelist download successful.");

        if (Networking.IsMaster)
        {
            // Check if data actually changed before syncing and processing, reduces unnecessary work
            if (syncedWhitelistData != result.Result)
            {
                Log("Master received updated data, updating synced variable and requesting serialization.");
                syncedWhitelistData = result.Result;
                RequestSerialization(); // Sync the updated data to other clients
                ProcessWhitelistData(); // Master processes the new list immediately
            } else {
                Log("Master received data, but it hasn't changed. No update needed.");
                // Ensure the initial check flag is set even if data hasn't changed on subsequent checks
                if (!_initialCheckDone)
                {
                    ProcessWhitelistData(); // Still need to process locally on first successful load
                }
            }
        }
         // Ensure the initial check flag is set after the first successful download attempt by master
         if (Networking.IsMaster) {
            _initialCheckDone = true;
         }
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        // Ensure it's our expected download before processing error
        if (result.Url == null || result.Url.Get() != whitelistUrl.Get()) return;

        LogError($"Failed to download whitelist: {result.ErrorCode} - {result.Error}");
        // Ensure initial check is marked as done even on error to allow timer to run/retry
        _initialCheckDone = true;
        SetChildrenVisibility(false); // Ensure child game objects remain off on error
    }

    // --- Whitelist Processing & Visibility ---

    private void ProcessWhitelistData()
    {
        if (_isProcessing) return;
        _isProcessing = true;

        Log("Processing whitelist data...");
        if (string.IsNullOrEmpty(syncedWhitelistData))
        {
            Log("Synced data is null or empty, clearing local whitelist.");
            _localWhitelist = new string[0];
        }
        else
        {
            string[] splitNames = syncedWhitelistData.Split(new[] { '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries);
            
            // Use a temporary array first, as we don't know the final count yet.
            // Size it to the maximum possible length.
            string[] tempWhitelist = new string[splitNames.Length]; 
            int validNameCount = 0; // Keep track of how many valid names we find

            for (int i = 0; i < splitNames.Length; i++)
            {
                string trimmedName = splitNames[i].Trim(); // Trim whitespace

                // Check if the name is not null or empty *after* trimming
                if (!string.IsNullOrEmpty(trimmedName)) 
                {
                    tempWhitelist[validNameCount] = trimmedName; // Add the valid name
                    validNameCount++; // Increment the counter
                }
            }

            // Now create the final _localWhitelist array with the exact correct size
            _localWhitelist = new string[validNameCount];
            
            // Copy the valid names from the temporary array to the final array
            System.Array.Copy(tempWhitelist, _localWhitelist, validNameCount); 
            // --- End of Replacement ---
        }

        loadedNameCount = _localWhitelist.Length; 
        Log($"Parsed {loadedNameCount} valid names from whitelist.");

        CheckLocalPlayerVisibility(); 
        _isProcessing = false; 
    }

    private void CheckLocalPlayerVisibility()
    {
        // Check if LocalPlayer is valid before accessing properties
        if (!Utilities.IsValid(Networking.LocalPlayer))
        {
             Log("Local player not valid yet.");
             isWhitelisted = false;
        }
        else
        {
            string localDisplayName = Networking.LocalPlayer.displayName;
            isWhitelisted = false; // Reset before checking

            // Manually loop through the array to check for the name
            if (_localWhitelist != null) // Add a null check for safety
            {
                for (int i = 0; i < _localWhitelist.Length; i++)
                {
                    // Perform a case-sensitive comparison
                    if (_localWhitelist[i] == localDisplayName) 
                    {
                        isWhitelisted = true; // Name found in the list
                        break; // Exit the loop early since we found a match
                    }
                }
            }

            Log($"Local player '{localDisplayName}' IsWhitelisted: {isWhitelisted}");
        }

        SetChildrenVisibility(isWhitelisted); 
    }

        // Renamed from SetChildrenVisibility
    private void SetChildrenVisibility(bool isVisible)
    {
         if (targetGameObject == null) return;

        float targetAlpha = isVisible ? 1.0f : 0.0f; 

        Log($"Setting children VISIBILITY for '{targetGameObject.name}' to: {isVisible} (Alpha: {targetAlpha}, Enabled: {isVisible})");
        Transform parentTransform = targetGameObject.transform;

        for (int i = 0; i < parentTransform.childCount; i++)
        {
            GameObject child = parentTransform.GetChild(i).gameObject;
            if (child == null) continue;

            // --- Handle 3D Renderers ---
            Renderer[] renderers = child.GetComponentsInChildren<Renderer>(true); 
            foreach (Renderer rend in renderers)
            {
                if (rend != null && rend.enabled != isVisible) 
                {
                    rend.enabled = isVisible;
                }
            }

            // --- Handle UI CanvasGroups ---
            CanvasGroup[] canvasGroups = child.GetComponentsInChildren<CanvasGroup>(true); 
            bool controlledByCanvasGroup = false; // Flag to see if we handled this via CanvasGroup
            foreach (CanvasGroup cg in canvasGroups)
            {
                if (cg != null)
                {
                    cg.alpha = targetAlpha; 
                    cg.interactable = isVisible; 
                    cg.blocksRaycasts = isVisible;
                    controlledByCanvasGroup = true; // Mark that we found and controlled a CanvasGroup
                }
            }

            // --- Handle Canvases directly (Fallback Method) ---
            // Only disable Canvases if we didn't find and control a CanvasGroup 
            // in this child's hierarchy (to avoid redundant control and favor CanvasGroup)
            if (!controlledByCanvasGroup) 
            {
                Canvas[] canvases = child.GetComponentsInChildren<Canvas>(true);
                foreach (Canvas canvas in canvases) {
                    if (canvas != null && canvas.enabled != isVisible) {
                        // This will hide the canvas and potentially stop some UI updates/layouts
                        canvas.enabled = isVisible; 
                    }
                }
            }
            
            // ... TODO: handling for Lights, Particle Systems, etc. ...
        }
    }

    // --- Manual Refresh ---
    public void RefreshWhitelist()
    {
        if (Networking.IsMaster)
        {
            Log("Manual refresh requested by master.");
             _refreshTimer = 0f; // Reset timer on manual refresh
            DownloadWhitelist();
        } else {
            Log("Manual refresh requested, but not master. Ignoring.");
        }
    }

    // --- Logging ---
    private void Log(string message)
    {
        Debug.Log($"[RemoteWhitelistVisibility] {message}");
    }
    private void LogError(string message)
    {
        Debug.LogError($"[RemoteWhitelistVisibility] {message}");
    }

    // --- Automatic Refresh Timer (Added/Corrected) ---
    void Update()
    {
        // Only the Master runs the timer, and only after the initial download attempt
        if (Networking.IsMaster && _initialCheckDone)
        {
            _refreshTimer += Time.deltaTime; // Add time elapsed since last frame

            if (_refreshTimer >= REFRESH_INTERVAL)
            {
                _refreshTimer = 0f; // Reset timer
                Log("Automatic refresh interval reached. Requesting download.");
                DownloadWhitelist(); // Trigger the download
            }
        }
    }
    // --- End Automatic Refresh Timer ---
}