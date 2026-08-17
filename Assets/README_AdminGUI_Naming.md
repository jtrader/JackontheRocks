# JackOnTheRocks Admin GUI naming conventions

Use these object names in your Unity scene so the auto-wirer can assign references without manual dragging.

## Required component object
- `JackOnTheRocksAdminGUI`

If there is no scene open in Unity, the editor tool will create a new empty scene and a minimal `AdminCanvas` automatically.

## Tab navigation
- `NavButton_Overview`
- `NavButton_TransactionsPayID`
- `NavButton_RegionalManagers`
- `NavButton_CreativeAnalytics`
- `NavButton_ExclusiveContent`
- `NavButton_UserMatching`
- `NavButton_SurveyTelemetry`

## Tab panels
- `TabPanel_Overview`
- `TabPanel_TransactionsPayID`
- `TabPanel_RegionalManagers`
- `TabPanel_CreativeAnalytics`
- `TabPanel_ExclusiveContent`
- `TabPanel_UserMatching`
- `TabPanel_SurveyTelemetry`

## Session auth / floating access
- `FloatingOpenButton`
- `AdminKeyInput`
- `LoginButton`
- `LoginStatusText`

## Overview fields
- `OverviewRevenueText`
- `OverviewActivePlayersText`
- `OverviewRocksInCirculationText`
- `OverviewPendingOrdersText`
- `AgeGateToggle`
- `MainEnginePauseToggle`
- `EmergencyStoreFreezeToggle`

## Transactions / PayID
- `TransactionsSearchInput`
- `TransactionsFilterDropdown`
- `TransactionsListContent`
- `TransactionListItemPrefab`

## Regional managers
- `RegionalManagersContent`
- `RegionalManagerItemPrefab`
- `ManagerRegionNameInput`
- `ManagerLatInput`
- `ManagerLongInput`
- `ManagerRadiusKmInput`
- `ManagerPhoneInput`
- `ManagerSnapchatTokenInput`

## Creative analytics
- `CreativeListContent`
- `CreativeItemPrefab`

## User matching
- `UserSearchInput`
- `UserListContent`
- `UserItemPrefab`

## Survey telemetry
- `SurveyListContent`
- `SurveyItemPrefab`

## Notes
- The auto-wirer is tolerant: if a field is not found, it leaves it null and logs the missing names for you to fix.
- If your UI uses different names, either rename the objects to match this list or assign the references manually in the Inspector.
