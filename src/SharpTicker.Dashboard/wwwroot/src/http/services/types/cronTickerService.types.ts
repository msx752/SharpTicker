export class GetCronTickerRequest {
}

export class GetCronTickerResponse {
    id!:string;
    function!:string;
    expression!:string;
    initIdentifier!:string;
    retryIntervals!:string[];
    description!:string;
    requestType!:string;
    createdAt!:string;
    updatedAt!:string;
    retries!:number;
    isEnabled!:boolean;
    // Filled in after the page has loaded, by the run summary request
    lastRunAt?:string;
    nextRunAt?:string;
    lastRun?:string;
    lastStatus?:number;
    lastOccurrenceId?:string;
    nextRun?:string;
    actions:string|undefined = undefined;
}

export class UpdateCronTickerRequest {
    function!:string;
    expression!:string;
    request?:string;
    retries?: number;
    description?: string;
    retryIntervals?:number[];
    isEnabled?: boolean;
}

export class GetCronTickerGraphDataRangeResponse{
    date!:string;
    results!:{item1:number, item2:number }[];
}

export class GetCronTickerGraphDataResponse{
    item1!:number;
    item2!:number;
}

export class AddCronTickerRequest {
    function!:string;
    expression!:string;
    request?:string;
    retries?: number;
    description?: string;
    retryIntervals?:number[];
}
