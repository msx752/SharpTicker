export class GetCronTickerOccurrenceRequest {
    id!:string
}

export class GetCronTickerOccurrenceResponse {
    id!: string;
    status!:number|string;
    exceptionMessage?:string;
    skippedReason?:string;
    retryIntervals!:string[]|string|null;
    lockHolder!:string;
    lockedAt!:string;
    executionTime!:string;
    executionTimeFormatted!:string;
    executedAt!:string;
    /** Set by the service: how long ago it ran, for the tooltip of the exact time. */
    executedAgo?:string;
    /** Set by the service: how long it took. */
    durationText?:string;
    elapsedTime!:string|number;
    retryCount!:number;
    actions:string|undefined = undefined;
}


export class GetCronTickerOccurrenceGraphDataRequest{
}

export class GetCronTickerOccurrenceGraphDataResponse{
    date!:string;
    results!:{item1:number, item2:number }[];
    type!: string;
    statuses!:string[]
}