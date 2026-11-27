@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param frontdoor_outputs_id string

output frontDoorId string = frontdoor_outputs_id