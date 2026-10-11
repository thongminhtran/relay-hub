// Subscription-scope budget alert: emails when spend passes 50 % / 80 % / 100 % of the monthly amount.
// Deploy once: az deployment sub create -l canadacentral -f infra/budget.bicep -p contactEmail=<you>
targetScope = 'subscription'

@description('Monthly budget in the subscription currency (CAD/USD).')
param amount int = 5

@description('Where alerts go.')
param contactEmail string

@description('First day of the current month, yyyy-MM-01.')
param startDate string = '${substring(utcNow('yyyy-MM-dd'), 0, 7)}-01'

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: 'budget-relayhub'
  properties: {
    category: 'Cost'
    amount: amount
    timeGrain: 'Monthly'
    timePeriod: { startDate: startDate }
    notifications: {
      at50: { enabled: true, operator: 'GreaterThan', threshold: 50, contactEmails: [contactEmail], thresholdType: 'Actual' }
      at80: { enabled: true, operator: 'GreaterThan', threshold: 80, contactEmails: [contactEmail], thresholdType: 'Actual' }
      at100: { enabled: true, operator: 'GreaterThan', threshold: 100, contactEmails: [contactEmail], thresholdType: 'Forecasted' }
    }
  }
}
