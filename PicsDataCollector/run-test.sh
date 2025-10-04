#!/bin/bash
echo "Steam PICS Depot Mapping Collector - Test Run"
echo "==============================================="
echo ""
echo "This will run a test collection with the last 10,000 changes."
echo "It should complete in a few minutes."
echo ""
read -p "Press enter to continue..."

dotnet run -- --incremental

echo ""
echo "Test complete! Check pics_depot_mappings.json for results."
read -p "Press enter to exit..."
